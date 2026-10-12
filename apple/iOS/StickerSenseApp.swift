import PhotosUI
import StickerKit
import SwiftUI

@main
struct StickerSenseApp: App {
    @State private var state = AppState()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(state)
                .onReceive(NotificationCenter.default.publisher(for: AppRouter.didChange)) { _ in state.handlePendingRoute() }
                .onChange(of: scenePhase) { _, phase in if phase == .active { state.handlePendingRoute() } }
        }
    }
}

/// An image waiting to be described in a sheet.
struct DescribeRequest: Identifiable {
    let id = UUID()
    let data: Data?
}

@MainActor
@Observable
final class AppState {
    enum Tab: Hashable { case stickers, history, setup, settings }

    var tab: Tab = .stickers
    var describeRequest: DescribeRequest?
    let library = SharedStore.library
    let search: PickerSearch
    let pipeline: StickerPipeline

    init() {
        TextReader.warmUp()
        let describer = StickerDescriber(options: MobileSettings().describerOptions)
        pipeline = StickerPipeline(describer: describer, library: SharedStore.library)
        search = PickerSearch(library: SharedStore.library, describer: describer)
    }

    func handlePendingRoute() {
        guard let route = AppRouter.shared.pending else { return }
        AppRouter.shared.pending = nil
        switch route {
        case .describeClipboard:
            describeRequest = DescribeRequest(data: StickerClipboard.imageData())
        case .openPicker:
            describeRequest = nil
            tab = .stickers
        }
    }
}

struct RootView: View {
    @Environment(AppState.self) private var state

    var body: some View {
        @Bindable var state = state
        TabView(selection: $state.tab) {
            Tab("Stickers", systemImage: "face.smiling", value: .stickers) { StickersScreen() }
            Tab("History", systemImage: "clock", value: .history) { HistoryScreen() }
            Tab("Set Up", systemImage: "hand.tap", value: .setup) { SetupScreen() }
            Tab("Settings", systemImage: "gear", value: .settings) { SettingsScreen() }
        }
        .sheet(item: $state.describeRequest) { request in
            DescribeView(imageData: request.data) { state.describeRequest = nil }
        }
    }
}

// MARK: - Stickers (picker)

@MainActor
@Observable
final class MobilePickerModel {
    var query = ""
    var filter: SourceFilter = .all
    var items: [PickerItem] = []
    var readings: [String: StickerReading] = [:]
    var status = ""
    private var paging: (query: String, gifOffset: Int, stickerOffset: Int, hasMore: Bool)?
    private var loadingMore = false
    private var describeQueue: [GiphyItem] = []
    private var describeWorker: Task<Void, Never>?

    func search(using search: PickerSearch) async {
        let query = self.query
        describeQueue.removeAll()
        paging = nil
        // Your own stickers first: instant.
        let library = filter == .giphy ? [] : await search.libraryResults(query, filter: filter)
        guard !Task.isCancelled else { return }
        items = library
        var giphyCount = 0, note = ""
        if (filter == .all || filter == .giphy), !query.trimmingCharacters(in: .whitespaces).isEmpty {
            let results = await search.giphyPage(query, apiKey: MobileSettings().giphyKey)
            guard !Task.isCancelled else { return }
            await append(results, query: query, using: search)
            giphyCount = results.items.count
            note = results.note
        }
        status = PickerItem.resultsSummary(libraryCount: library.count, giphyCount: giphyCount, query: query,
                                          firstLabel: items.first.map(label(for:)), note: note, sendHint: "Double tap one to copy it.")
        if !query.isEmpty { Speaker.say(status, queue: true) }
    }

    func loadMore(using search: PickerSearch) async {
        guard let paging, paging.hasMore, !loadingMore, paging.query == query else { return }
        loadingMore = true
        defer { loadingMore = false }
        let results = await search.giphyPage(paging.query, apiKey: MobileSettings().giphyKey,
                                             gifOffset: paging.gifOffset, stickerOffset: paging.stickerOffset)
        guard query == paging.query else { return }
        await append(results, query: paging.query, using: search)
        Speaker.say("\(results.items.count) more from GIPHY.", queue: true)
    }

    private func append(_ results: PickerSearch.GiphyResults, query: String, using search: PickerSearch) async {
        items.removeAll { $0 == .loadMore }
        items += results.items
        if results.hasMore { items.append(.loadMore) }
        paging = (query, results.nextGifOffset, results.nextStickerOffset, results.hasMore)
        readings.merge(await search.readings(for: results.items)) { a, _ in a }
        describeQueue += results.items.prefix(8).compactMap(\.giphy).filter { readings[PickerItem.giphy($0).id] == nil }
        startWorker(search)
    }

    /// Rows coming on screen get described first.
    func prioritize(_ item: PickerItem, using search: PickerSearch) {
        guard let gif = item.giphy, readings[item.id] == nil else { return }
        describeQueue.removeAll { $0 == gif }
        describeQueue.insert(gif, at: 0)
        startWorker(search)
    }

    private func startWorker(_ search: PickerSearch) {
        guard describeWorker == nil else { return }
        describeWorker = Task { [weak self] in
            while let self, !Task.isCancelled, !self.describeQueue.isEmpty {
                let gif = self.describeQueue.removeFirst()
                let id = PickerItem.giphy(gif).id
                guard self.readings[id] == nil, self.items.contains(where: { $0.id == id }) else { continue }
                if let reading = await search.describe(gif) { self.readings[id] = reading }
            }
            self?.describeWorker = nil
        }
    }

    func label(for item: PickerItem) -> String {
        item.accessibilityLabel(reading: readings[item.id])
    }

    func stop() {
        describeWorker?.cancel()
        describeWorker = nil
        describeQueue.removeAll()
    }
}

struct StickersScreen: View {
    @Environment(AppState.self) private var state
    @State private var model = MobilePickerModel()
    @State private var photo: PhotosPickerItem?

    var body: some View {
        NavigationStack {
            List {
                Picker("Show stickers from", selection: $model.filter) {
                    ForEach(SourceFilter.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)

                if model.items.isEmpty {
                    Text(model.status.isEmpty ? "Loading…" : model.status).foregroundStyle(.secondary)
                }
                ForEach(model.items) { item in
                    if item == .loadMore {
                        Button("More GIPHY Results") { Task { await model.loadMore(using: state.search) } }
                            .onAppear { Task { await model.loadMore(using: state.search) } }
                    } else {
                        StickerRow(item: item, label: model.label(for: item), libraryImages: state.library.imagesDirectory)
                            .contentShape(Rectangle())
                            .onTapGesture { copy(item) }
                            .onAppear { model.prioritize(item, using: state.search) }
                            .accessibilityElement(children: .ignore)
                            .accessibilityLabel(model.label(for: item))
                            .accessibilityHint("Double tap to copy it, then paste it in your chat.")
                            .accessibilityAddTraits(.isButton)
                            .accessibilityAction { copy(item) }
                            .accessibilityAction(named: "More detail") { detail(item) }
                            .accessibilityActions {
                                if case .library = item {
                                    Button("Remove from library", role: .destructive) { remove(item) }
                                }
                            }
                    }
                }
            }
            .navigationTitle("Stickers")
            .onDisappear { model.stop() }
            .searchable(text: $model.query, prompt: "What it shows, says or means")
            .task(id: "\(model.query)|\(model.filter.rawValue)") {
                try? await Task.sleep(for: .milliseconds(350))
                guard !Task.isCancelled else { return }
                await model.search(using: state.search)
            }
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("Describe Copied", systemImage: "doc.on.clipboard") {
                        state.describeRequest = DescribeRequest(data: StickerClipboard.imageData())
                    }
                }
                ToolbarItem(placement: .topBarTrailing) {
                    PhotosPicker(selection: $photo, matching: .images) {
                        Label("Describe a Picture or Screenshot", systemImage: "photo.badge.magnifyingglass")
                    }
                }
            }
            .onChange(of: photo) { _, item in
                guard let item else { return }
                Task {
                    let data = try? await item.loadTransferable(type: Data.self)
                    state.describeRequest = DescribeRequest(data: data)
                    photo = nil
                }
            }
        }
    }

    private func copy(_ item: PickerItem) {
        Task {
            guard let url = await state.search.fileURL(for: item), StickerClipboard.copy(fileURL: url) else {
                Speaker.say("Couldn't copy that one.")
                return
            }
            await state.search.markSent(item)
            Speaker.say("Copied. Go back to your chat and paste it.")
        }
    }

    private func detail(_ item: PickerItem) {
        Task {
            Speaker.say("Describing in detail.")
            do {
                let text: String
                switch item {
                case .library(let record):
                    let described = try await state.pipeline.describeStored(record)
                    text = try await state.pipeline.detail(for: described, frames: nil, context: .file)
                case .giphy(let gif):
                    text = try await state.search.detail(gif)
                case .loadMore:
                    return
                }
                Speaker.say(text)
            } catch {
                Speaker.say(SpokenText.failure(error))
            }
        }
    }

    private func remove(_ item: PickerItem) {
        guard case .library(let record) = item else { return }
        Task {
            await state.library.delete(record.id)
            model.items.removeAll { $0.id == item.id }
            Speaker.say("Removed.")
        }
    }
}

private struct StickerRow: View {
    let item: PickerItem
    let label: String
    let libraryImages: URL

    var body: some View {
        HStack(spacing: 12) {
            thumbnail.frame(width: 60, height: 60)
            Text(label).lineLimit(3)
        }
    }

    @ViewBuilder private var thumbnail: some View {
        switch item {
        case .library(let record):
            if let file = record.imageFile, let image = UIImage(contentsOfFile: libraryImages.appendingPathComponent(file).path) {
                Image(uiImage: image).resizable().scaledToFit()
            } else {
                Image(systemName: "photo")
            }
        case .giphy(let gif):
            AsyncImage(url: gif.smallURL ?? gif.previewURL) { $0.resizable().scaledToFit() } placeholder: { ProgressView() }
        case .loadMore:
            EmptyView()
        }
    }
}

// MARK: - History

struct HistoryScreen: View {
    @Environment(AppState.self) private var state
    @State private var records: [StickerRecord] = []

    var body: some View {
        NavigationStack {
            List {
                if records.isEmpty {
                    Text("Stickers you have described appear here.").foregroundStyle(.secondary)
                }
                ForEach(records) { record in
                    Text(label(record))
                        .accessibilityAction(named: "More detail") { detail(record) }
                        .accessibilityAction(named: "Copy description") {
                            UIPasteboard.general.string = record.detailed ?? record.reading.map { SpokenText.short($0, includeVibe: true) }
                            Speaker.say("Description copied.")
                        }
                }
                .onDelete { offsets in
                    let ids = offsets.map { records[$0].id }
                    Task {
                        for id in ids { await state.library.delete(id) }
                        await reload()
                    }
                }
            }
            .navigationTitle("History")
            .task { await reload() }
            .refreshable { await reload() }
        }
    }

    private func label(_ record: StickerRecord) -> String {
        let text = record.reading.map { SpokenText.short($0, includeVibe: true) } ?? "Not described"
        return "\(text) \(record.lastSeenAt.formatted(.relative(presentation: .named)))."
    }

    private func reload() async {
        records = await state.library.history()
    }

    private func detail(_ record: StickerRecord) {
        Task {
            do {
                Speaker.say(try await state.pipeline.detail(for: record, frames: nil, context: .file))
            } catch {
                Speaker.say(SpokenText.failure(error))
            }
        }
    }
}

// MARK: - Set Up

struct SetupScreen: View {
    @Environment(\.openURL) private var openURL

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Text(StickerDescriber.status.explanation)
                } header: {
                    Text("On-device model")
                }

                Section {
                    step(1, "Open the Shortcuts app and make a new shortcut.")
                    step(2, "Add the action Take Screenshot.")
                    step(3, "Add the action Describe Stickers, from StickerSense. It uses the screenshot automatically.")
                    step(4, "Name the shortcut Describe Stickers.")
                    step(5, "In Settings, go to Accessibility, Touch, Back Tap. Choose Double Tap, then choose Describe Stickers. On iPhones with an Action button you can use it there instead.")
                    step(6, "Now, in any chat, double-tap the back of your iPhone. StickerSense describes the stickers on screen, newest first.")
                    Button("Open Shortcuts") { openURL(URL(string: "shortcuts://create-shortcut")!) }
                    Button("Open Settings") { openURL(URL(string: UIApplication.openSettingsURLString)!) }
                } header: {
                    Text("Back Tap: describe the stickers on screen")
                }

                Section {
                    Text("Copy a sticker or GIF in your chat app, then use the Describe Copied Sticker control. To add it, open Control Center, choose Edit, Add a Control, and search for StickerSense. You can also put Describe Copied Sticker on Back Tap.")
                } header: {
                    Text("Control Center")
                }

                Section {
                    Text("In any app, share a sticker, GIF or picture and choose StickerSense to hear it described. It's saved to your sticker library so you can find it again.")
                } header: {
                    Text("Share sheet")
                }
            }
            .navigationTitle("Set Up")
        }
    }

    private func step(_ number: Int, _ text: String) -> some View {
        Text("\(number). \(text)")
    }
}

// MARK: - Settings

struct SettingsScreen: View {
    @State private var includeMeaning = MobileSettings().includeMeaning
    @State private var allowPrivateCloud = MobileSettings().allowPrivateCloud
    @State private var giphyKey = MobileSettings().giphyKey

    var body: some View {
        NavigationStack {
            Form {
                Section("Descriptions") {
                    Toggle("Add what the sticker means to short descriptions", isOn: $includeMeaning)
                        .onChange(of: includeMeaning) { _, value in MobileSettings().includeMeaning = value }
                    Toggle("Use Apple Private Cloud Compute for detailed descriptions", isOn: $allowPrivateCloud)
                        .onChange(of: allowPrivateCloud) { _, value in MobileSettings().allowPrivateCloud = value }
                        .disabled(!StickerDescriber.privateCloudAvailable)
                    Text("Short descriptions always stay on this iPhone. When this is on, only detailed descriptions may use Apple's privacy-preserving servers.")
                        .font(.footnote).foregroundStyle(.secondary)
                }
                Section("GIPHY") {
                    SecureField("GIPHY API key", text: $giphyKey)
                        .onSubmit { MobileSettings().giphyKey = giphyKey }
                        .onChange(of: giphyKey) { _, value in MobileSettings().giphyKey = value }
                    Link("Get a free GIPHY API key", destination: URL(string: "https://developers.giphy.com/dashboard/")!)
                }
            }
            .navigationTitle("Settings")
        }
    }
}
