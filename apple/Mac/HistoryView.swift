import AppKit
import Observation
import StickerKit
import SwiftUI

@MainActor
@Observable
final class HistoryModel {
    let app: AppModel
    var records: [StickerRecord] = []
    var selection: UUID?

    init(app: AppModel) {
        self.app = app
        NotificationCenter.default.addObserver(forName: .stickerLibraryChanged, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.reload() }
        }
    }

    func reload() {
        Task { records = await app.library.history() }
    }

    func label(_ record: StickerRecord) -> String {
        let when = record.lastSeenAt.formatted(.relative(presentation: .named))
        var text = record.reading.map { SpokenText.short($0, includeVibe: app.settings.includeMeaning) } ?? "Not described"
        if let detailed = record.detailed, !detailed.isEmpty { text += " Detail available." }
        return "\(text) \(record.source.displayName), \(when)."
    }

    func speakShort(_ record: StickerRecord) {
        guard let reading = record.reading else { return }
        app.announcer.say(SpokenText.short(reading, includeVibe: true))
    }

    func speakDetail(_ record: StickerRecord) {
        Task {
            if let detailed = record.detailed {
                app.announcer.say(detailed)
                return
            }
            guard record.imageFile != nil else {
                app.announcer.say("No detail saved for this one. Photos aren't kept, for privacy.")
                return
            }
            app.earcons.play(.working)
            do {
                let text = try await app.pipeline.detail(for: record, frames: nil, context: .file)
                app.earcons.play(.ready)
                app.announcer.say(text)
                reload()
            } catch {
                app.earcons.play(.failed)
                app.announcer.say(SpokenText.failure(error))
            }
        }
    }

    func copyText(_ record: StickerRecord) {
        let text = record.detailed ?? record.reading.map { SpokenText.short($0, includeVibe: true) } ?? ""
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        app.announcer.say("Description copied.")
    }

    func remove(_ record: StickerRecord) {
        Task {
            await app.library.delete(record.id)
            reload()
            app.announcer.say("Removed.")
        }
    }

    func clearAll() {
        Task {
            for record in records { await app.library.delete(record.id) }
            reload()
            app.announcer.say("History cleared.")
        }
    }
}

struct HistoryView: View {
    @Bindable var model: HistoryModel
    @State private var confirmClear = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if model.records.isEmpty {
                Text("Nothing yet. Put the VoiceOver cursor on a sticker and press the shortcut, and it will show up here.")
                    .foregroundStyle(.secondary)
                    .padding()
            }
            List(selection: $model.selection) {
                ForEach(model.records) { record in
                    Text(model.label(record))
                        .lineLimit(3)
                        .tag(record.id)
                        .accessibilityLabel(model.label(record))
                        .accessibilityHint("Return speaks the detailed description")
                        .accessibilityAction { model.speakDetail(record) }
                        .accessibilityAction(named: "Describe in detail") { model.speakDetail(record) }
                        .accessibilityAction(named: "Say short description") { model.speakShort(record) }
                        .accessibilityAction(named: "Copy description") { model.copyText(record) }
                        .accessibilityAction(named: "Remove") { model.remove(record) }
                }
            }
            .contextMenu(forSelectionType: UUID.self) { ids in
                if let record = record(ids) {
                    Button("Describe in Detail") { model.speakDetail(record) }
                    Button("Say Short Description") { model.speakShort(record) }
                    Button("Copy Description") { model.copyText(record) }
                    Button("Remove") { model.remove(record) }
                }
            } primaryAction: { ids in
                if let record = record(ids) { model.speakDetail(record) }
            }
            .accessibilityLabel("Recent stickers")

            HStack {
                Spacer()
                Button("Clear History…") { confirmClear = true }
                    .disabled(model.records.isEmpty)
            }
        }
        .padding(14)
        .frame(minWidth: 520, minHeight: 380)
        .onAppear { model.reload() }
        .confirmationDialog("Clear all sticker history?", isPresented: $confirmClear) {
            Button("Clear History", role: .destructive) { model.clearAll() }
        } message: {
            Text("This also removes these stickers from the picker.")
        }
    }

    private func record(_ ids: Set<UUID>) -> StickerRecord? {
        ids.first.flatMap { id in model.records.first { $0.id == id } }
    }
}
