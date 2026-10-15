import CoreGraphics
import CryptoKit
import Foundation
import ImageIO
import StickerKit

/// The image models that can make stickers. Neither ships with the app: the chosen one is
/// downloaded from Hugging Face the first time it's needed, and checked against its SHA-256.
/// The Windows app uses the same files (StickerMakerCatalog.cs).
nonisolated enum StickerModel: String, CaseIterable, Identifiable, Sendable {
    /// SDXS-512 (IDKiro, OpenRAIL++): one small step, about 3 seconds a sticker.
    case fast
    /// SD-Turbo (Stability AI Community License) with the tiny TAESD decoder: follows the search
    /// more closely and varies more, at about twice the download.
    case better

    var id: String { rawValue }

    var title: String {
        switch self {
        case .fast: "Fast (SDXS, 900 MB)"
        case .better: "Better (SD-Turbo, 2 GB)"
        }
    }

    var spokenSize: String {
        switch self {
        case .fast: "900 megabytes"
        case .better: "2 gigabytes"
        }
    }

    var credit: String {
        switch self {
        case .fast: "SDXS-512 by IDKiro and others, under the OpenRAIL++ licence."
        case .better: "SD-Turbo by Stability AI, under the Stability AI Community License. Powered by Stability AI."
        }
    }

    struct File: Sendable {
        let name: String
        let url: URL
        let size: Int64
        let sha256: String
    }

    var files: [File] {
        switch self {
        case .fast:
            [File(name: "sdxs.safetensors",
                  url: URL(string: "https://huggingface.co/akleine/sdxs-512/resolve/17ef024d73ebee1db9b0643f9c90454dae6c5772/sdxs.safetensors")!,
                  size: 882_587_118, sha256: "6cca5bfd11b588cdfb4602018c7e623d24c95fdfdc5ab2d4b9e6978b3186980f")]
        case .better:
            [File(name: "sd_turbo-f16-q8_0.gguf",
                  url: URL(string: "https://huggingface.co/Green-Sky/SD-Turbo-GGUF/resolve/19a31586d02d64a73b4419bc193b3ecfaf38e1f0/sd_turbo-f16-q8_0.gguf")!,
                  size: 2_023_745_376, sha256: "d50be7655f0a554cf8041c145d88b210bd5f3c545423119dee62ae08cae51580"),
             File(name: "taesd.safetensors",
                  url: URL(string: "https://huggingface.co/madebyollin/taesd/resolve/614f76814bbe30edbe2e627ace1c2234c81a2c0e/diffusion_pytorch_model.safetensors")!,
                  size: 9_793_292, sha256: "db169d69145ec4ff064e49d99c95fa05d3eb04ee453de35824a6d0f325513549")]
        }
    }

    var folderName: String {
        switch self {
        case .fast: "sdxs-512"
        case .better: "sd-turbo"
        }
    }

    /// sd-cli's options for this model (both are one-step models: guidance off).
    func arguments(in folder: URL) -> [String] {
        switch self {
        case .fast:
            ["-m", folder.appendingPathComponent("sdxs.safetensors").path, "--cfg-scale", "1", "--steps", "1"]
        case .better:
            ["-m", folder.appendingPathComponent("sd_turbo-f16-q8_0.gguf").path,
             "--taesd", folder.appendingPathComponent("taesd.safetensors").path, "--cfg-scale", "1", "--steps", "1"]
        }
    }
}

nonisolated enum StickerMakerError: Error, LocalizedError {
    case helperMissing
    case downloadFailed(String)
    case failed(String)

    var errorDescription: String? {
        switch self {
        case .helperMissing: "The sticker maker is missing from this copy of StickerSense. Reinstall StickerSense."
        case .downloadFailed(let why): "The sticker model didn't download: \(why)"
        case .failed(let why): "Couldn't make that sticker: \(why)"
        }
    }
}

/// Makes pictures with stable-diffusion.cpp's sd-cli, bundled in the app next to StickerSense
/// itself, on the Mac's graphics card. One process per picture: it costs about half a second,
/// keeps a crash in the model away from StickerSense, and gives all its memory back afterwards.
actor StickerMaker {
    nonisolated static var modelsDirectory: URL {
        StickerLibrary.defaultDirectory().appendingPathComponent("models", isDirectory: true)
    }

    nonisolated static func folder(for model: StickerModel) -> URL {
        modelsDirectory.appendingPathComponent(model.folderName, isDirectory: true)
    }

    nonisolated static func isInstalled(_ model: StickerModel) -> Bool {
        model.files.allSatisfy { file in
            let url = folder(for: model).appendingPathComponent(file.name)
            return (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize).map { Int64($0) == file.size } ?? false
        }
    }

    /// sd-cli inside the app (Contents/MacOS), or `STICKERSENSE_SD_CLI` when developing.
    nonisolated static var helperURL: URL? {
        if let path = ProcessInfo.processInfo.environment["STICKERSENSE_SD_CLI"], FileManager.default.isExecutableFile(atPath: path) {
            return URL(fileURLWithPath: path)
        }
        return Bundle.main.url(forAuxiliaryExecutable: "sd-cli")
    }

    /// The graphics card compiles sd-cli's shaders the first time each build of it runs (about
    /// 25 seconds), then remembers them.
    nonisolated static var isWarm: Bool {
        FileManager.default.fileExists(atPath: warmMarker.path)
    }

    private nonisolated static var warmMarker: URL {
        let build = helperURL.flatMap { try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate }
        return modelsDirectory.appendingPathComponent(".warm-\(Int(build?.timeIntervalSince1970 ?? 0))")
    }

    // MARK: Downloading

    /// Downloads the model's files that aren't there yet. `progress` gets 0…1.
    func download(_ model: StickerModel, progress: @escaping @Sendable (Double) -> Void) async throws {
        let folder = Self.folder(for: model)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let total = Double(model.files.map(\.size).reduce(0, +))
        var done = 0.0
        for file in model.files {
            let target = folder.appendingPathComponent(file.name)
            if let size = try? target.resourceValues(forKeys: [.fileSizeKey]).fileSize, Int64(size) == file.size {
                done += Double(file.size)
                continue
            }
            let before = done
            var lastError: Error?
            for attempt in 1...3 {
                do {
                    try await Self.fetch(file, to: target) { fraction in progress((before + fraction * Double(file.size)) / total) }
                    lastError = nil
                    break
                } catch is CancellationError {
                    throw CancellationError()
                } catch {
                    lastError = error
                    try await Task.sleep(for: .seconds(2 * attempt))
                }
            }
            if let lastError { throw StickerMakerError.downloadFailed(lastError.localizedDescription) }
            done += Double(file.size)
            progress(done / total)
        }
    }

    private static func fetch(_ file: StickerModel.File, to target: URL, progress: @escaping @Sendable (Double) -> Void) async throws {
        let partial = target.appendingPathExtension("part")
        let delegate = Download(destination: partial, report: progress)
        let session = URLSession(configuration: .default, delegate: delegate, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        let task = session.downloadTask(with: file.url)
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                delegate.finished = { continuation.resume(with: $0) }
                task.resume()
            }
        } onCancel: {
            task.cancel()
        }
        defer { try? FileManager.default.removeItem(at: partial) }
        let size = (try? partial.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init) ?? -1
        guard size == file.size else { throw StickerMakerError.downloadFailed("\(file.name) is \(size) bytes, expected \(file.size)") }
        guard try sha256(of: partial) == file.sha256 else { throw StickerMakerError.downloadFailed("\(file.name) didn't match its checksum") }
        try? FileManager.default.removeItem(at: target)
        try FileManager.default.moveItem(at: partial, to: target)
    }

    private static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 8 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    /// Moves the finished file into place and reports progress (URLSession's own delegate queue).
    private nonisolated final class Download: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
        let destination: URL
        let report: @Sendable (Double) -> Void
        var finished: ((Result<Void, Error>) -> Void)?
        private var moveError: Error?

        init(destination: URL, report: @escaping @Sendable (Double) -> Void) {
            self.destination = destination
            self.report = report
        }

        func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                        totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
            guard totalBytesExpectedToWrite > 0 else { return }
            report(Double(totalBytesWritten) / Double(totalBytesExpectedToWrite))
        }

        func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
            // The file is deleted when this returns: move it now.
            do {
                if let status = (downloadTask.response as? HTTPURLResponse)?.statusCode, status != 200 {
                    throw StickerMakerError.downloadFailed("Hugging Face answered \(status)")
                }
                try? FileManager.default.removeItem(at: destination)
                try FileManager.default.moveItem(at: location, to: destination)
            } catch {
                moveError = error
            }
        }

        func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
            let outcome: Result<Void, Error>
            if let error {
                outcome = .failure((error as? URLError)?.code == .cancelled ? CancellationError() : error)
            } else if let moveError {
                outcome = .failure(moveError)
            } else {
                outcome = .success(())
            }
            finished?(outcome)
            finished = nil
        }
    }

    // MARK: Making a picture

    /// One finished sticker for one picture idea: drawn, cut out, captioned and saved as a PNG
    /// in `folder`. Not yet described.
    func makeSticker(picture: String, caption: String, query: String, number: Int, model: StickerModel, in folder: URL) async throws -> GeneratedSticker {
        let image = try await generate(prompt: StickerIdeas.prompt(for: picture), seed: Int.random(in: 1...999_999), model: model)
        try Task.checkCancellation()
        guard let art = StickerArt.compose(StickerArt.cutOut(image) ?? image, caption: caption) else {
            throw StickerMakerError.failed("the picture came out empty")
        }
        let file = folder.appendingPathComponent("\(UUID().uuidString).png")
        try ImagePrep.pngData(art).write(to: file)
        return GeneratedSticker(query: query, caption: caption, picture: picture, fileURL: file, number: number)
    }

    /// Compiles the graphics card's shaders with a tiny picture, so the first real sticker
    /// doesn't wait for it (about 45 seconds, once).
    func warmUp(_ model: StickerModel) async {
        guard !Self.isWarm else { return }
        _ = try? await generate(prompt: "a cat", seed: 1, model: model, size: 64)
    }

    /// One picture for `prompt` (512 pixels square unless asked). Cancelling the task stops sd-cli.
    func generate(prompt: String, seed: Int, model: StickerModel, size: Int = 512) async throws -> CGImage {
        guard let helper = Self.helperURL else { throw StickerMakerError.helperMissing }
        let work = FileManager.default.temporaryDirectory.appendingPathComponent("StickerSense-make", isDirectory: true)
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        let id = UUID().uuidString
        let output = work.appendingPathComponent("\(id).png"), log = work.appendingPathComponent("\(id).log")
        FileManager.default.createFile(atPath: log.path, contents: nil)
        defer {
            try? FileManager.default.removeItem(at: output)
            try? FileManager.default.removeItem(at: log)
        }

        let process = Process()
        process.executableURL = helper
        process.arguments = model.arguments(in: Self.folder(for: model)) + [
            "-p", prompt, "-W", String(size), "-H", String(size), "-s", String(seed), "-o", output.path,
        ]
        let logHandle = try FileHandle(forWritingTo: log)
        defer { try? logHandle.close() }
        process.standardOutput = logHandle
        process.standardError = logHandle

        let status: Int32 = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                process.terminationHandler = { continuation.resume(returning: $0.terminationStatus) }
                do {
                    try process.run()
                } catch {
                    process.terminationHandler = nil
                    continuation.resume(throwing: error)
                }
            }
        } onCancel: {
            if process.isRunning { process.terminate() }
        }
        try Task.checkCancellation()
        // Decoded now, from memory: the file is deleted on the way out.
        guard status == 0, let data = try? Data(contentsOf: output),
              let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, [kCGImageSourceShouldCacheImmediately: true] as CFDictionary) else {
            let tail = (try? String(contentsOf: log, encoding: .utf8)).map { String($0.suffix(400)) } ?? ""
            throw StickerMakerError.failed("the image model stopped (code \(status)). \(tail)")
        }
        if !Self.isWarm { FileManager.default.createFile(atPath: Self.warmMarker.path, contents: nil) }
        return image
    }
}
