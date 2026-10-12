import Foundation

/// A small activity log (~/Library/Logs/StickerSense.log) — handy for "why didn't it speak?"
/// questions and for tuning sticker detection in new chat apps. Never logs images.
@MainActor
final class DebugLog {
    private let url: URL
    private let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return f
    }()
    private(set) var recent: [String] = []

    init() {
        let logs = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0].appendingPathComponent("Logs", isDirectory: true)
        try? FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
        url = logs.appendingPathComponent("StickerSense.log")
        // Keep the file small: start fresh when it passes 1 MB.
        if let size = try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int, size > 1_000_000 {
            try? FileManager.default.removeItem(at: url)
        }
    }

    func add(_ message: String) {
        let line = "\(formatter.string(from: Date())) \(message)"
        recent.append(line)
        if recent.count > 300 { recent.removeFirst(recent.count - 300) }
        NSLog("StickerSense: %@", message)
        guard let data = (line + "\n").data(using: .utf8) else { return }
        if let handle = try? FileHandle(forWritingTo: url) {
            handle.seekToEndOfFile()
            handle.write(data)
            try? handle.close()
        } else {
            try? data.write(to: url)
        }
    }
}
