import AppKit
import Observation
import StickerKit
import SwiftUI

@MainActor
@Observable
final class PickerModel {
    let app: AppModel
    var targetApp: NSRunningApplication?
    /// Characters to delete before pasting: 2 when the picker was opened by typing "::".
    var charactersToReplace = 0
    var close: () -> Void = {}
    /// False once the picker has closed.
    private(set) var isOpen = true

    var query = "" {
        didSet {
            guard query != oldValue else { return }
            stopGenerating(because: "the search changed")
            scheduleSearch()
        }
    }
    var filter: SourceFilter = .all { didSet { scheduleSearch(immediate: true) } }
    var items: [PickerItem] = []
    var selection: PickerItem.ID? { didSet { selectionChanged() } }
    var status = ""
    /// On-device descriptions of GIPHY results, mirrored from PickerSearch for the view.
    var giphyReadings: [String: StickerReading] = [:]

    private var searchTask: Task<Void, Never>?
    private var focusDescribeTask: Task<Void, Never>?
    private var libraryCount = 0
    private var giphyCount = 0
    private var giphyPaging: (query: String, gifOffset: Int, stickerOffset: Int, hasMore: Bool)?
    private var loadingMore = false
    /// GIPHY results waiting for an on-device description; the focused one jumps the queue.
    private var describeQueue: [GiphyItem] = []
    private var describeWorker: Task<Void, Never>?
    /// Stickers made in this picker, newest batch first. They stay at the top until it closes.
    private var generatedItems: [PickerItem] = []
    private var generateTask: Task<Void, Never>?
    private var madeCount = 0
    /// Making stickers now (the button says so).
    var isGenerating = false
    /// How many of this batch are done, for "still making stickers" answers.
    private var batchProgress = (done: 0, total: 0)

    init(app: AppModel) {
        self.app = app
    }

    private var search: PickerSearch { app.pickerSearch }
    private var wantsGiphy: Bool { (filter == .all || filter == .giphy) && !query.trimmingCharacters(in: .whitespaces).isEmpty }
    /// The how-to hint is only said once per picker; after that, just the results.
    private var hintGiven = false
    private var sendHint: String {
        if hintGiven { return "" }
        hintGiven = true
        return "Return pastes it, Down Arrow for more. Command G makes your own."
    }

    /// Something typed: the Generate Sticker button appears.
    var canGenerate: Bool { !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    // MARK: Search

    func scheduleSearch(immediate: Bool = false) {
        searchTask?.cancel()
        searchTask = Task { [weak self] in
            if !immediate { try? await Task.sleep(for: .milliseconds(300)) }
            guard !Task.isCancelled else { return }
            await self?.runSearch()
        }
    }

    private func runSearch() async {
        let query = self.query, filter = self.filter
        describeQueue.removeAll()
        giphyPaging = nil
        // Your own stickers first: they're local and instant.
        let library = filter == .giphy ? [] : await search.libraryResults(query, filter: filter)
        guard !Task.isCancelled else { return }
        libraryCount = library.count
        giphyCount = 0
        items = generatedItems + library
        if let selection, !items.contains(where: { $0.id == selection }) { self.selection = nil }
        guard wantsGiphy else {
            await announceWhenTypingPauses(note: "")
            return
        }
        // Give GIPHY a moment so one announcement can cover both; otherwise speak now and add GIPHY later.
        let giphy = Task { await search.giphyPage(query, apiKey: app.settings.giphyKey) }
        let quick = await Race.value(of: giphy, timeout: .milliseconds(700))
        guard !Task.isCancelled else { giphy.cancel(); return }
        if let quick {
            appendGiphy(quick, query: query)
            await announceWhenTypingPauses(note: quick.note)
        } else {
            await announceWhenTypingPauses(note: "", giphyPending: true)
            let results = await giphy.value
            guard !Task.isCancelled, self.query == query else { return }
            appendGiphy(results, query: query)
            if results.items.isEmpty {
                if !results.note.isEmpty { app.announcer.say(results.note, interrupt: false) }
            } else {
                app.announcer.say("Plus \(results.items.count) from GIPHY.", interrupt: false)
            }
        }
    }

    private func appendGiphy(_ results: PickerSearch.GiphyResults, query: String) {
        items.removeAll { $0 == .loadMore }
        items += results.items
        giphyCount += results.items.count
        giphyPaging = (query, results.nextGifOffset, results.nextStickerOffset, results.hasMore)
        if results.hasMore { items.append(.loadMore) }
        Task {
            // Some may already be described from earlier searches.
            let known = await search.readings(for: results.items)
            giphyReadings.merge(known) { a, _ in a }
            enqueueDescriptions(results.items.compactMap(\.giphy))
        }
    }

    /// Results show at once, but are only *spoken* once the user stops typing for a moment, so
    /// announcements don't talk over VoiceOver echoing each letter. Nothing is said for the
    /// empty search shown on opening — VoiceOver reads the search field itself.
    private func announceWhenTypingPauses(note: String, giphyPending: Bool = false) async {
        guard !query.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        try? await Task.sleep(for: .milliseconds(450))
        guard !Task.isCancelled else { return }
        status = PickerItem.resultsSummary(libraryCount: libraryCount, giphyCount: giphyCount, query: query,
                                          firstLabel: items.first.map(label(for:)), note: note, sendHint: sendHint, giphyPending: giphyPending)
        if libraryCount + giphyCount == 0, !giphyPending { status += " Command G makes one." }
        app.announcer.say(status, interrupt: false)
    }

    /// Next page of GIPHY results (50 more).
    func loadMore(announce: Bool) {
        guard let paging = giphyPaging, paging.hasMore, !loadingMore, paging.query == query else { return }
        loadingMore = true
        Task {
            defer { loadingMore = false }
            let results = await search.giphyPage(paging.query, apiKey: app.settings.giphyKey,
                                                 gifOffset: paging.gifOffset, stickerOffset: paging.stickerOffset)
            guard self.query == paging.query else { return }
            let firstNew = results.items.first
            appendGiphy(results, query: paging.query)
            if announce {
                app.announcer.say("\(results.items.count) more from GIPHY.", interrupt: true)
                if let firstNew { selection = firstNew.id }
            }
        }
    }

    func label(for item: PickerItem) -> String {
        item.accessibilityLabel(reading: giphyReadings[item.id])
    }

    // MARK: Describing GIPHY results and undescribed stickers

    /// Describe only what the user is likely to hear soon: the first few of each page here,
    /// and the focused row plus the next few as they arrow (see selectionChanged).
    private func enqueueDescriptions(_ gifs: [GiphyItem]) {
        let fresh = gifs.prefix(8).filter { gif in giphyReadings[PickerItem.giphy(gif).id] == nil && !describeQueue.contains(gif) }
        describeQueue += fresh
        startDescribeWorker()
    }

    /// Stops all background work — called when the picker closes so nothing keeps the model busy.
    func stop() {
        isOpen = false
        generateTask?.cancel()
        searchTask?.cancel()
        focusDescribeTask?.cancel()
        describeWorker?.cancel()
        describeWorker = nil
        describeQueue.removeAll()
    }

    private func startDescribeWorker() {
        guard describeWorker == nil else { return }
        describeWorker = Task { [weak self] in
            while let self, !Task.isCancelled, !self.describeQueue.isEmpty {
                let gif = self.describeQueue.removeFirst()
                let id = PickerItem.giphy(gif).id
                guard self.giphyReadings[id] == nil, self.items.contains(where: { $0.id == id }) else { continue }
                if let reading = await self.search.describe(gif) {
                    self.giphyReadings[id] = reading
                    // The user is on this one and was waiting for it: say it.
                    if self.selection == id { self.app.announcer.say(SpokenText.listLabel(reading), interrupt: false) }
                }
            }
            self?.describeWorker = nil
        }
    }

    /// Arrowing onto something: describe it first if it isn't yet, and fetch more near the end.
    private func selectionChanged() {
        focusDescribeTask?.cancel()
        guard let selection, let index = items.firstIndex(where: { $0.id == selection }) else { return }
        if index >= items.count - 6 { loadMore(announce: false) }
        switch items[index] {
        case .library(let record) where !record.isDescribed:
            focusDescribeTask = Task { [weak self] in
                guard let self, let updated = try? await self.app.backgroundPipeline.describeStored(record) else { return }
                guard !Task.isCancelled, self.selection == selection else { return }
                if let i = self.items.firstIndex(where: { $0.id == selection }) { self.items[i] = .library(updated) }
                self.app.announcer.say(updated.accessibilityLabel, interrupt: false)
            }
        case .giphy:
            // The focused row first, then the next few the user is about to reach.
            let upcoming = items[index...].prefix(4).compactMap(\.giphy).filter { giphyReadings[PickerItem.giphy($0).id] == nil }
            describeQueue.removeAll { upcoming.contains($0) }
            describeQueue.insert(contentsOf: upcoming, at: 0)
            startDescribeWorker()
        default:
            break
        }
    }

    // MARK: Actions

    func sendFirst() {
        guard let first = items.first(where: { $0 != .loadMore }) else {
            app.earcons.play(.failed)
            app.announcer.say("Nothing to paste.")
            return
        }
        send(first)
    }

    func send(_ item: PickerItem) {
        if item == .loadMore {
            loadMore(announce: true)
            return
        }
        Task {
            guard let file = await search.fileURL(for: item) else {
                app.earcons.play(.failed)
                app.announcer.say("Couldn't get that sticker.")
                return
            }
            let target = targetApp
            close()
            let outcome = await Paster.paste(fileURL: file, into: target, replacing: charactersToReplace)
            app.earcons.play(.tick)
            switch outcome {
            case .pasted(let name):
                app.announcer.say("Pasted into \(name). Press Return to send, or Command Z to undo.")
            case .copiedOnly(let reason):
                app.announcer.say(reason)
            }
            await search.markSent(item)
            app.refreshCounts()
        }
    }

    func copy(_ item: PickerItem) {
        Task {
            guard let file = await search.fileURL(for: item) else { return }
            Paster.copy(fileURL: file)
            app.earcons.play(.tick)
            app.announcer.say("Copied.")
            await search.markSent(item)
        }
    }

    func describeInDetail(_ item: PickerItem) {
        Task {
            app.earcons.play(.working)
            do {
                let text: String
                switch item {
                case .library(let record):
                    let described = try await app.pipeline.describeStored(record)
                    text = try await app.pipeline.detail(for: described, frames: nil, context: .file)
                case .giphy(let gif):
                    text = try await search.detail(gif)
                case .generated(let sticker):
                    text = try await search.detail(sticker)
                case .loadMore:
                    return
                }
                app.earcons.play(.ready)
                app.announcer.say(text)
            } catch {
                app.earcons.play(.failed)
                app.announcer.say(SpokenText.failure(error))
            }
        }
    }

    func remove(_ item: PickerItem) {
        guard case .library(let record) = item else { return }
        Task {
            await app.library.delete(record.id)
            items.removeAll { $0.id == item.id }
            app.announcer.say("Removed.")
            app.refreshCounts()
        }
    }
}

// MARK: Making stickers

extension PickerModel {
    /// Makes stickers of what's typed: the Generate Sticker button and Command G.
    func generate() {
        let query = self.query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else {
            app.announcer.say("Type what the sticker should show first, then press Command G.")
            return
        }
        guard !isGenerating else {
            app.announcer.say("Still making stickers: \(batchProgress.done) of \(batchProgress.total) done.")
            return
        }
        let model = app.settings.stickerModel
        guard StickerMaker.helperURL != nil else {
            app.earcons.play(.failed)
            app.announcer.say(StickerMakerError.helperMissing.errorDescription ?? "")
            return
        }
        guard StickerMaker.isInstalled(model) else {
            offerDownload(model, for: query)
            return
        }
        // Set before the task starts, so a second Command G can't start a second batch.
        isGenerating = true
        generateTask = Task { await makeStickers(of: query, model: model) }
    }

    /// The first time: ask before downloading, then make the stickers if the picker is still on that search.
    private func offerDownload(_ model: StickerModel, for query: String) {
        if let download = app.stickerDownload {
            app.announcer.say("The sticker maker is still downloading: \(download.percent) percent.")
            return
        }
        let alert = NSAlert()
        alert.messageText = "Download the sticker maker?"
        alert.informativeText = "Making stickers needs a one-time download of \(model.spokenSize) from Hugging Face. After that, stickers are made on this Mac, without the internet. You can change the model in Settings."
        alert.addButton(withTitle: "Download")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        Task {
            guard await app.downloadStickerModel(model), self.query.trimmingCharacters(in: .whitespacesAndNewlines) == query,
                  !isGenerating, generateTask == nil, isOpen else { return }
            generate()
        }
    }

    /// Stops a batch in progress; what's already made stays.
    func stopGenerating(because reason: String) {
        guard isGenerating, let task = generateTask else { return }
        task.cancel()
        app.log.add("Stopped making stickers: \(reason)")
        app.announcer.say("Stopped making stickers.", interrupt: false)
    }

    private func makeStickers(of query: String, model: StickerModel) async {
        let count = StickerIdeas.variations
        batchProgress = (0, count)
        defer {
            isGenerating = false
            generateTask = nil
        }
        Self.removeOldStickerFiles()
        app.earcons.play(.working)
        let warm = StickerMaker.isWarm
        status = "Making \(count) stickers of \(query)…"
        app.announcer.say("Making \(count) stickers of \(query)." + (warm ? "" : " The very first one takes about a minute while the graphics card gets ready."), interrupt: true)

        let idea = await StickerIdeaWriter.ideas(for: query)
        app.log.add("Making stickers of \"\(query)\": caption \"\(idea.caption)\", pictures \(idea.pictures)")
        var made = 0, failures = 0
        // Each sticker is described while the next one is drawn; announcements stay in order.
        var describing: Task<Void, Never>?
        for (index, picture) in idea.pictures.enumerated() {
            guard !Task.isCancelled else { break }
            let started = Date()
            do {
                let sticker = try await app.stickerMaker.makeSticker(picture: picture, caption: idea.caption, query: query,
                                                                     number: madeCount + 1, model: model, in: Self.madeDirectory)
                guard !Task.isCancelled else { break }
                madeCount += 1
                // Newest batch on top, in order within the batch.
                generatedItems.insert(.generated(sticker), at: made)
                items.insert(.generated(sticker), at: made)
                made += 1
                batchProgress.done = made
                app.log.add("Drew sticker \(index + 1) in \(String(format: "%.1f", Date().timeIntervalSince(started)))s")
                let previous = describing, first = made == 1
                describing = Task { [weak self] in
                    guard let self else { return }
                    var described = sticker
                    described.reading = await self.search.describe(sticker)
                    await previous?.value
                    self.replaceGenerated(described)
                    guard !Task.isCancelled else { return }
                    let what = described.reading.map(SpokenText.listLabel) ?? picture
                    self.status = "Made \(index + 1) of \(count)."
                    self.app.announcer.say("Sticker \(index + 1) of \(count): \(what)." + (first ? " It's at the top of the list." : ""), interrupt: false)
                }
            } catch is CancellationError {
                break
            } catch {
                failures += 1
                app.log.add("Making sticker \(index + 1) failed: \(error.localizedDescription)")
                if failures >= 2 || error is StickerMakerError && made == 0 && index == 0 {
                    describing?.cancel()
                    app.earcons.play(.failed)
                    app.announcer.say((error as? LocalizedError)?.errorDescription ?? "Couldn't make stickers.")
                    return
                }
            }
        }
        if Task.isCancelled {
            describing?.cancel()
            return
        }
        await describing?.value
        app.earcons.play(made > 0 ? .ready : .failed)
        status = made == count ? "Made \(count) stickers of \(query)." : "Made \(made) of \(count) stickers."
        if made == count {
            app.announcer.say("All \(count) are ready. Return pastes one into your chat.", interrupt: false)
        } else if made == 0 {
            app.announcer.say("Couldn't make stickers this time. Try again, or a different search.", interrupt: false)
        }
    }

    /// Puts a sticker's description into its rows.
    private func replaceGenerated(_ sticker: GeneratedSticker) {
        let id = PickerItem.generated(sticker).id
        if let i = generatedItems.firstIndex(where: { $0.id == id }) { generatedItems[i] = .generated(sticker) }
        if let i = items.firstIndex(where: { $0.id == id }) { items[i] = .generated(sticker) }
    }

    static var madeDirectory: URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("StickerSense-made", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    /// Stickers made but never sent are kept for a day (a paste reads the file after the picker closes).
    static func removeOldStickerFiles() {
        let dir = madeDirectory
        let old = Date().addingTimeInterval(-24 * 3600)
        for file in (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.contentModificationDateKey])) ?? [] {
            if let date = try? file.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate, date < old {
                try? FileManager.default.removeItem(at: file)
            }
        }
    }
}

struct PickerView: View {
    @Bindable var model: PickerModel
    @FocusState private var focus: Field?

    enum Field: Hashable { case search, list }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                TextField("Search stickers", text: $model.query, prompt: Text("What it shows, says or means, e.g. birthday, lol, thank you"))
                    .textFieldStyle(.roundedBorder)
                    .focused($focus, equals: .search)
                    .accessibilityLabel("Search stickers")
                    .accessibilityHint("Type what it shows, says or means. Down Arrow goes to the results. Return pastes the first one.")
                    .onSubmit { model.sendFirst() }
                    .onKeyPress(.downArrow) {
                        guard let first = model.items.first else { return .ignored }
                        model.selection = first.id
                        focus = .list
                        return .handled
                    }
                if model.canGenerate {
                    Button(model.isGenerating ? "Making Stickers…" : "Generate Sticker") { model.generate() }
                        .keyboardShortcut("g", modifiers: .command)
                        .accessibilityHint("Makes \(StickerIdeas.variations) new stickers of what you typed, on this Mac. Command G.")
                }
                Picker("Source", selection: $model.filter) {
                    ForEach(SourceFilter.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .frame(maxWidth: 320)
                .accessibilityLabel("Show stickers from")
            }

            List(selection: $model.selection) {
                ForEach(model.items) { item in
                    PickerRow(item: item, label: model.label(for: item), imageURL: rowImageURL(item))
                        .tag(item.id)
                        .accessibilityElement(children: .ignore)
                        .accessibilityLabel(model.label(for: item))
                        .accessibilityAddTraits(.isButton)
                        .accessibilityHint(item == .loadMore ? "Return loads 50 more" : "Return pastes it into your chat")
                        .accessibilityAction { model.send(item) }
                        .accessibilityActions {
                            if item != .loadMore {
                                Button("Paste into chat") { model.send(item) }
                                Button("Describe in detail") { model.describeInDetail(item) }
                                Button("Copy") { model.copy(item) }
                            }
                            if case .library = item {
                                Button("Remove from library") { model.remove(item) }
                            }
                        }
                }
            }
            .contextMenu(forSelectionType: PickerItem.ID.self) { ids in
                if let item = item(for: ids), item != .loadMore {
                    Button("Paste into Chat") { model.send(item) }
                    Button("Describe in Detail") { model.describeInDetail(item) }
                    Button("Copy") { model.copy(item) }
                    if case .library = item { Button("Remove from Library") { model.remove(item) } }
                }
            } primaryAction: { ids in
                if let item = item(for: ids) { model.send(item) }
            }
            .accessibilityLabel("Stickers")
            .focused($focus, equals: .list)
            .onKeyPress(.upArrow) {
                // Up from the first result goes back to the search field.
                guard model.selection == model.items.first?.id else { return .ignored }
                focus = .search
                return .handled
            }

            Text(model.status)
                .font(.callout)
                .foregroundStyle(.secondary)
                .lineLimit(2)
                .accessibilityHidden(true) // already announced
        }
        .padding(14)
        .frame(minWidth: 560, minHeight: 420)
        .onAppear {
            focus = .search
            model.scheduleSearch(immediate: true)
            // The window can take a moment to become key after the app activates.
            Task {
                try? await Task.sleep(for: .milliseconds(300))
                if focus == nil { focus = .search }
            }
        }
        .onExitCommand { model.close() }
        .onDisappear { model.stop() }
    }

    private func item(for ids: Set<PickerItem.ID>) -> PickerItem? {
        guard let id = ids.first else { return nil }
        return model.items.first { $0.id == id }
    }

    private func rowImageURL(_ item: PickerItem) -> URL? {
        switch item {
        case .library(let record): model.app.library.imagesDirectory.appendingPathComponent(record.imageFile ?? "")
        case .giphy(let gif): gif.smallURL ?? gif.previewURL
        case .generated(let sticker): sticker.fileURL
        case .loadMore: nil
        }
    }
}

private struct PickerRow: View {
    let item: PickerItem
    let label: String
    let imageURL: URL?

    var body: some View {
        HStack(spacing: 12) {
            Group {
                if let imageURL, imageURL.isFileURL, let image = NSImage(contentsOf: imageURL) {
                    Image(nsImage: image).resizable().scaledToFit()
                } else if let imageURL {
                    AsyncImage(url: imageURL) { image in image.resizable().scaledToFit() } placeholder: { ProgressView() }
                } else {
                    Image(systemName: item == .loadMore ? "arrow.down.circle" : "photo")
                        .font(.title2)
                }
            }
            .frame(width: 56, height: 56)
            Text(label).lineLimit(2)
            Spacer()
        }
        .padding(.vertical, 2)
    }
}
