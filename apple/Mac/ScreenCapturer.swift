import AppKit
import ImageIO
import ScreenCaptureKit
import StickerKit

enum CaptureError: Error, LocalizedError {
    case noPermission
    case offScreen
    case failed(String)

    var errorDescription: String? {
        switch self {
        case .noPermission:
            "StickerSense needs Screen Recording permission to see stickers. Turn it on in System Settings, Privacy and Security, Screen and System Audio Recording."
        case .offScreen:
            "The VoiceOver cursor isn't on screen."
        case .failed(let message):
            "Couldn't capture the screen: \(message)"
        }
    }
}

/// Captures a screen region (the VoiceOver cursor's rectangle) as images.
@MainActor
final class ScreenCapturer {
    private let voiceOver: VoiceOverBridge
    private var cachedContent: (content: SCShareableContent, at: Date)?

    init(voiceOver: VoiceOverBridge) {
        self.voiceOver = voiceOver
    }

    static var hasPermission: Bool { CGPreflightScreenCaptureAccess() }

    @discardableResult
    static func requestPermission() -> Bool { CGRequestScreenCaptureAccess() }

    /// Touches ScreenCaptureKit once so any macOS consent prompt shows up now (at launch or in
    /// Setup) instead of in the middle of the user's first description.
    func prime() async {
        guard Self.hasPermission else { return }
        _ = try? await shareableContent()
    }

    /// One frame of `rect` (global points, top-left origin).
    func capture(_ rect: CGRect) async throws -> CGImage {
        guard rect.width >= 4, rect.height >= 4 else { throw CaptureError.offScreen }
        if Self.hasPermission {
            return try await captureWithScreenCaptureKit(rect)
        }
        return try await captureWithVoiceOver(rect)
    }

    /// Captures `rect` repeatedly to find out whether it's animated. Returns 1 frame for a still
    /// image, or up to 3 distinct frames spread over about 0.7 seconds for an animated one.
    /// `first` lets callers reuse a frame they already captured.
    func captureMotion(_ rect: CGRect, first: CGImage, alreadyKnownAnimated: Bool = false) async throws -> [CGImage] {
        guard Self.hasPermission else { return [first] }
        var frames = [first]
        var signatures = [MotionSignature(first)]
        // Two quick looks (~300 ms) to see whether it moves; most stickers are still.
        // Even for GIFs, look first: WhatsApp shows a still preview until a GIF is opened.
        var animated = false
        for _ in 0..<2 where !animated {
            try await Task.sleep(for: .milliseconds(150))
            let frame = try await captureWithScreenCaptureKit(rect)
            let signature = MotionSignature(frame)
            if signature.differs(from: signatures[0]) {
                animated = true
                frames.append(frame)
                signatures.append(signature)
            }
        }
        guard animated else { return [first] }
        if alreadyKnownAnimated {
            // GIFs are longer clips than sticker loops: two more looks across ~1.1 s so the model
            // sees the scene change (3 frames in all; a 4th costs about a second of model time).
            for _ in 0..<2 where frames.count < 3 {
                try await Task.sleep(for: .milliseconds(550))
                let frame = try await captureWithScreenCaptureKit(rect)
                let signature = MotionSignature(frame)
                if signatures.allSatisfy({ signature.differs(from: $0) }) {
                    frames.append(frame)
                    signatures.append(signature)
                }
            }
            return frames
        }
        // It moves: one or two more looks a little later, so three frames span ~0.7 s of the loop.
        var attempts = 0
        while frames.count < 3, attempts < 3 {
            attempts += 1
            try await Task.sleep(for: .milliseconds(200))
            let frame = try await captureWithScreenCaptureKit(rect)
            let signature = MotionSignature(frame)
            if signatures.allSatisfy({ signature.differs(from: $0) }) {
                frames.append(frame)
                signatures.append(signature)
            }
        }
        return frames
    }

    // MARK: ScreenCaptureKit

    private func shareableContent() async throws -> SCShareableContent {
        if let cachedContent, Date().timeIntervalSince(cachedContent.at) < 3 { return cachedContent.content }
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        cachedContent = (content, Date())
        return content
    }

    private func captureWithScreenCaptureKit(_ rect: CGRect) async throws -> CGImage {
        let content: SCShareableContent
        do {
            content = try await shareableContent()
        } catch {
            throw CaptureError.noPermission
        }
        let center = CGPoint(x: rect.midX, y: rect.midY)
        guard let display = content.displays.first(where: { $0.frame.contains(center) }) ?? content.displays.first else {
            throw CaptureError.offScreen
        }
        let clipped = rect.intersection(display.frame)
        guard !clipped.isNull, clipped.width >= 4, clipped.height >= 4 else { throw CaptureError.offScreen }

        // Hide VoiceOver's own cursor outline and caption panel.
        let excluded = content.applications.filter { $0.bundleIdentifier == "com.apple.VoiceOver" }
        let filter = SCContentFilter(display: display, excludingApplications: excluded, exceptingWindows: [])

        let config = SCStreamConfiguration()
        let scale = CGFloat(filter.pointPixelScale)
        config.sourceRect = CGRect(
            x: clipped.minX - display.frame.minX, y: clipped.minY - display.frame.minY,
            width: clipped.width, height: clipped.height
        )
        config.width = Int(clipped.width * scale)
        config.height = Int(clipped.height * scale)
        config.showsCursor = false
        config.capturesAudio = false
        config.ignoreShadowsSingleWindow = true
        do {
            return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
        } catch {
            cachedContent = nil
            throw CaptureError.failed(error.localizedDescription)
        }
    }

    // MARK: VoiceOver fallback

    private func captureWithVoiceOver(_ rect: CGRect) async throws -> CGImage {
        let url: URL
        do {
            url = try await voiceOver.grabScreenshotFile()
        } catch {
            throw CaptureError.noPermission
        }
        defer { try? FileManager.default.removeItem(at: url) }
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let full = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
            throw CaptureError.failed("unreadable screenshot")
        }
        // The screenshot covers the main display at its pixel scale.
        guard let screen = NSScreen.screens.first else { throw CaptureError.offScreen }
        let scale = CGFloat(full.width) / screen.frame.width
        // Inset a little: VoiceOver draws its cursor outline into its own screenshot.
        let inset = rect.insetBy(dx: 3, dy: 3)
        let pixelRect = CGRect(x: inset.minX * scale, y: inset.minY * scale, width: inset.width * scale, height: inset.height * scale)
            .integral.intersection(CGRect(x: 0, y: 0, width: full.width, height: full.height))
        guard let cropped = full.cropping(to: pixelRect) else { throw CaptureError.offScreen }
        return cropped
    }
}
