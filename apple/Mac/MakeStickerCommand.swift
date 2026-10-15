import AppKit
import Foundation
import StickerKit

/// `StickerSense --make-sticker "corgi saying thank you" [--count 4] [--model fast|better] [--out folder] [--download]`:
/// makes stickers the way the picker does, without any windows, printing each step and how long
/// it took. For testing the sticker maker and for bug reports; it doesn't touch the library.
@MainActor
enum MakeStickerCommand {
    /// `StickerSense --whatsapp-sticker file.png`: sends the file as a sticker in the WhatsApp chat
    /// that's open, the way the picker does. For testing on a real chat.
    static func sendToWhatsApp(_ args: [String]) async -> Int32 {
        guard let i = args.firstIndex(of: "--whatsapp-sticker"), i + 1 < args.count else { return 64 }
        let file = URL(fileURLWithPath: args[i + 1])
        // The sticker maker's steps activate apps, which needs an application object.
        NSApplication.shared.setActivationPolicy(.accessory)
        guard let whatsApp = NSRunningApplication.runningApplications(withBundleIdentifier: WhatsAppSticker.bundleIdentifier).first else {
            print("WhatsApp isn't running.")
            return 2
        }
        print("Qualifies as a sticker:", WhatsAppSticker.qualifies(file))
        let started = Date()
        let outcome = await WhatsAppSticker.send(file, in: whatsApp, replacing: 0)
        print(String(format: "%@ in %.1fs", String(describing: outcome), Date().timeIntervalSince(started)))
        return outcome == .sent ? 0 : 1
    }

    static func run(_ args: [String]) async -> Int32 {
        func value(_ name: String) -> String? {
            guard let i = args.firstIndex(of: name), i + 1 < args.count else { return nil }
            return args[i + 1]
        }
        guard let query = value("--make-sticker"), !query.isEmpty else {
            print("Usage: StickerSense --make-sticker \"what to draw\" [--count 4] [--model fast|better] [--out folder] [--download]")
            return 64
        }
        let count = value("--count").flatMap(Int.init) ?? StickerIdeas.variations
        let model = value("--model").flatMap(StickerModel.init(rawValue:)) ?? .fast
        let out = URL(fileURLWithPath: value("--out") ?? FileManager.default.currentDirectoryPath, isDirectory: true)
        try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
        let maker = StickerMaker()
        print("sd-cli: \(StickerMaker.helperURL?.path ?? "MISSING")")

        if !StickerMaker.isInstalled(model) {
            guard args.contains("--download") else {
                print("The \(model.rawValue) model isn't downloaded. Add --download to fetch it (\(model.spokenSize)).")
                return 2
            }
            print("Downloading the \(model.rawValue) model to \(StickerMaker.folder(for: model).path)")
            let started = Date()
            do {
                try await maker.download(model) { fraction in
                    let percent = Int(fraction * 100)
                    if percent % 10 == 0 { print("  \(percent)%") }
                }
            } catch {
                print("FAILED: \((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)")
                return 1
            }
            print(String(format: "Downloaded in %.0fs", Date().timeIntervalSince(started)))
        }

        var started = Date()
        let idea = await StickerIdeaWriter.ideas(for: query)
        print(String(format: "Plan (%.1fs): caption \"%@\"; pictures: %@", Date().timeIntervalSince(started), idea.caption, idea.pictures.joined(separator: " | ")))
        let library = StickerLibrary(directory: FileManager.default.temporaryDirectory.appendingPathComponent("StickerSense-make-test-\(UUID().uuidString)"))
        let search = PickerSearch(library: library, describer: StickerDescriber())
        var made = 0
        let batchStarted = Date()
        // Like the picker: each sticker is described while the next is drawn.
        var describing: Task<Void, Never>?
        for number in 1...count {
            let picture = idea.pictures[(number - 1) % idea.pictures.count]
            started = Date()
            do {
                let sticker = try await maker.makeSticker(picture: picture, caption: idea.caption, query: query, number: number, model: model, in: out)
                let drawn = Date().timeIntervalSince(started)
                let previous = describing
                describing = Task {
                    var described = sticker
                    let start = Date()
                    described.reading = await search.describe(sticker)
                    await previous?.value
                    print(String(format: "Sticker %d: drawn in %.1fs, described in %.1fs: %@ -> %@", number, drawn,
                                 Date().timeIntervalSince(start), GeneratedSticker.label(described), sticker.fileURL.lastPathComponent))
                }
                made += 1
            } catch {
                print("Sticker \(number) FAILED: \((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)")
            }
        }
        await describing?.value
        print(String(format: "All done in %.1fs", Date().timeIntervalSince(batchStarted)))
        return made == count ? 0 : 1
    }
}
