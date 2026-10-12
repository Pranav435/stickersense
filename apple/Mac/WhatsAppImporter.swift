import AppKit
import Foundation
import StickerKit

/// Finds the sticker files WhatsApp for Mac keeps on disk (stickers you've received, sent,
/// favourited or installed) and adds them to the library. Read-only: nothing in WhatsApp's
/// data is changed. macOS asks once for permission to read another app's data.
nonisolated enum WhatsAppImporter {
    static let containerNames = [
        "group.net.whatsapp.WhatsApp.shared",
        "group.net.whatsapp.WhatsAppSMB.shared",
    ]

    struct Result: Sendable {
        var found: Int
        var added: Int
        var permissionDenied: Bool
    }

    static var containers: [URL] {
        let base = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Group Containers", isDirectory: true)
        return containerNames.map { base.appendingPathComponent($0, isDirectory: true) }
    }

    static var isInstalled: Bool {
        containers.contains { FileManager.default.fileExists(atPath: $0.path) }
    }

    /// Sticker files: WebP images under 1 MB (WhatsApp stores every sticker as WebP).
    /// `roots` are folders the user chose in an Open panel (that choice is what grants access).
    static func stickerFiles(in roots: [URL], limit: Int = 4000) -> (files: [URL], denied: Bool) {
        var files: [URL] = []
        var denied = false
        for container in roots where FileManager.default.fileExists(atPath: container.path) {
            guard let enumerator = FileManager.default.enumerator(
                at: container,
                includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey],
                options: [.skipsPackageDescendants],
                errorHandler: { _, error in
                    if (error as NSError).code == NSFileReadNoPermissionError || (error as NSError).domain == NSPOSIXErrorDomain { denied = true }
                    return true
                }
            ) else { denied = true; continue }
            for case let url as URL in enumerator {
                guard url.pathExtension.lowercased() == "webp" else { continue }
                let values = try? url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
                guard values?.isRegularFile == true, (values?.fileSize ?? 0) < 1_000_000 else { continue }
                files.append(url)
                if files.count >= limit { break }
            }
        }
        if files.isEmpty, !denied {
            // Opening the container itself fails without permission.
            denied = roots.contains { (try? FileManager.default.contentsOfDirectory(atPath: $0.path)) == nil && FileManager.default.fileExists(atPath: $0.path) }
        }
        // Newest first, so recent stickers get described first.
        files.sort {
            let a = (try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            let b = (try? $1.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            return a > b
        }
        return (files, denied)
    }

    static func importAll(from roots: [URL], into library: StickerLibrary) async -> Result {
        let scan = await Task.detached(priority: .utility) { stickerFiles(in: roots) }.value
        var added = 0
        for url in scan.files {
            let relative = url.path.replacingOccurrences(of: FileManager.default.homeDirectoryForCurrentUser.path, with: "~")
            if await library.importFile(url, source: .whatsApp(relativePath: relative)) != nil { added += 1 }
        }
        return Result(found: scan.files.count, added: added, permissionDenied: scan.denied && scan.files.isEmpty)
    }
}

extension WhatsAppImporter {
    /// macOS won't prompt for another app's data; it silently blocks. Choosing the folder in an
    /// Open panel counts as the user's consent, so we open one already pointed at WhatsApp's
    /// folder: the user only has to press Choose.
    @MainActor
    static func askForFolder() -> URL? {
        guard let container = containers.first(where: { FileManager.default.fileExists(atPath: $0.path) }) else { return nil }
        let panel = NSOpenPanel()
        panel.title = "Import WhatsApp Stickers"
        panel.message = "Press Choose to let StickerSense read the stickers WhatsApp has saved on this Mac. Nothing in WhatsApp is changed."
        panel.prompt = "Choose"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        // Opened inside WhatsApp's folder: with nothing selected, Choose picks this folder.
        panel.directoryURL = container
        NSApp.activate()
        guard panel.runModal() == .OK else { return nil }
        return panel.url
    }
}
