import StickerKit
import SwiftUI
import UIKit

/// Describes one image (a shared sticker, the clipboard, a picked screenshot) and reads the
/// result out. Used by the app and by the share extension.
struct DescribeView: View {
    let imageData: Data?
    var onDone: () -> Void

    @State private var phase: Phase = .working
    @State private var detail: String?
    @State private var loadingDetail = false
    @State private var record: StickerRecord?
    @State private var frames: [CGImage] = []
    @AccessibilityFocusState private var focus: Focus?

    enum Focus: Hashable { case result, detail }

    enum Phase: Equatable {
        case working
        case done(String)
        case failed(String)
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    if let imageData, let image = UIImage(data: imageData) {
                        Image(uiImage: image)
                            .resizable()
                            .scaledToFit()
                            .frame(maxHeight: 220)
                            .frame(maxWidth: .infinity)
                            .accessibilityHidden(true)
                    }
                    switch phase {
                    case .working:
                        ProgressView("Describing…")
                            .frame(maxWidth: .infinity)
                    case .done(let text), .failed(let text):
                        Text(text)
                            .font(.title3)
                            .accessibilityFocused($focus, equals: .result)
                            .accessibilityAddTraits(.isHeader)
                    }
                    if let detail {
                        Text(detail).font(.body).accessibilityFocused($focus, equals: .detail)
                    }
                    if case .done = phase, detail == nil {
                        Button {
                            Task { await loadDetail() }
                        } label: {
                            Label(loadingDetail ? "Describing in detail…" : "More Detail", systemImage: "text.magnifyingglass")
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(loadingDetail)
                    }
                }
                .padding()
            }
            .navigationTitle("Sticker")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done", action: onDone)
                }
                if case .done(let text) = phase {
                    ToolbarItem(placement: .secondaryAction) {
                        Button("Copy Description") {
                            UIPasteboard.general.string = detail ?? text
                            Speaker.say("Description copied.")
                        }
                    }
                }
            }
        }
        .task { await describe() }
    }

    private func describe() async {
        guard let imageData else {
            finish(.failed("There's no picture on the clipboard. Copy a sticker or GIF first, for example with the Copy action in your chat app."))
            return
        }
        do {
            let settings = MobileSettings()
            let loaded = try ImagePrep.frames(from: imageData, maxFrames: 4)
            frames = ImagePrep.distinctFrames(loaded)
            guard let first = frames.first else { throw DescribeError.failed("unreadable picture") }
            let describer = StickerDescriber(options: settings.describerOptions)
            // A tall, full-size image is probably a screenshot of a chat: describe all its stickers.
            if frames.count == 1, first.height > 1500, Double(first.height) / Double(first.width) > 1.6 {
                let readings = try await describer.readScreenshot(first)
                finish(.done(SpokenText.screen(readings, includeVibe: settings.includeMeaning)))
                return
            }
            let pipeline = StickerPipeline(describer: describer, library: SharedStore.library)
            let described = try await pipeline.describe(frames: frames, context: .file, source: .imported)
            record = described
            let text = described.reading.map { SpokenText.short($0, includeVibe: settings.includeMeaning) } ?? ""
            finish(.done(text))
        } catch {
            finish(.failed(SpokenText.failure(error)))
        }
    }

    private func loadDetail() async {
        guard let record else { return }
        loadingDetail = true
        defer { loadingDetail = false }
        do {
            let pipeline = StickerPipeline(describer: StickerDescriber(options: MobileSettings().describerOptions), library: SharedStore.library)
            let text = try await pipeline.detail(for: record, frames: frames, context: .file)
            detail = text
            announce(text, focusOn: .detail)
        } catch {
            Speaker.say(SpokenText.failure(error))
        }
    }

    private func finish(_ phase: Phase) {
        self.phase = phase
        switch phase {
        case .done(let text), .failed(let text): announce(text, focusOn: .result)
        case .working: break
        }
    }

    /// With VoiceOver, moving focus onto the text reads it (and keeps it on the braille display);
    /// without VoiceOver, speak it.
    private func announce(_ text: String, focusOn target: Focus) {
        if UIAccessibility.isVoiceOverRunning {
            Task {
                try? await Task.sleep(for: .milliseconds(150))
                focus = target
            }
        } else {
            Speaker.say(text)
        }
    }
}
