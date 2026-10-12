import AppKit
import CoreGraphics
@testable import StickerKit

/// Renders sticker-like test images at runtime (emoji + caption on transparency),
/// so the tests need no binary fixtures.
enum Fixtures {
    static func sticker(
        _ emoji: String,
        caption: String = "",
        size: Int = 512,
        emojiX: CGFloat = 0.12,
        captionColor: NSColor = .systemPink,
        background: NSColor? = nil
    ) -> CGImage {
        let s = CGFloat(size)
        let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4,
            hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
        )!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        if let background {
            background.setFill()
            NSRect(x: 0, y: 0, width: s, height: s).fill()
        } else {
            NSColor.clear.setFill()
            NSRect(x: 0, y: 0, width: s, height: s).fill(using: .copy)
        }
        (emoji as NSString).draw(at: NSPoint(x: s * emojiX, y: s * 0.32), withAttributes: [.font: NSFont.systemFont(ofSize: s * 0.42)])
        if !caption.isEmpty {
            let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.boldSystemFont(ofSize: s * 0.13), .foregroundColor: captionColor]
            let width = (caption as NSString).size(withAttributes: attrs).width
            (caption as NSString).draw(at: NSPoint(x: (s - width) / 2, y: s * 0.08), withAttributes: attrs)
        }
        NSGraphicsContext.restoreGraphicsState()
        return rep.cgImage!
    }

    /// An "animated sticker": the emoji slides across and changes.
    static func animatedFrames() -> [CGImage] {
        [
            sticker("🐱", caption: "hi!", emojiX: 0.02),
            sticker("🐱", caption: "hi!", emojiX: 0.25),
            sticker("😻", caption: "hi!", emojiX: 0.45),
        ]
    }

    /// Simulates the same sticker captured from a chat screen: different scale, on a chat
    /// background colour, with a timestamp beside it.
    static func onScreenCapture(of image: CGImage, scale: Double = 0.6, chatColor: CGColor = CGColor(red: 0.86, green: 0.97, blue: 0.78, alpha: 1)) -> CGImage {
        let w = Int(Double(image.width) * scale), h = Int(Double(image.height) * scale)
        let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
        ctx.setFillColor(CGColor(gray: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage()!
    }

    /// A fake iPhone chat screenshot: text bubbles plus a dog "idk lol" sticker (older, higher)
    /// and a party "yay!" sticker (newest, lowest).
    static func chatScreenshot() -> CGImage {
        let w = 1179.0, h = 2556.0
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(w), pixelsHigh: Int(h), bitsPerSample: 8, samplesPerPixel: 4,
                                   hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSColor(calibratedRed: 0.93, green: 0.90, blue: 0.86, alpha: 1).setFill()
        NSRect(x: 0, y: 0, width: w, height: h).fill()
        ("Family Group" as NSString).draw(at: NSPoint(x: 380, y: h - 220), withAttributes: [.font: NSFont.boldSystemFont(ofSize: 54)])
        func bubble(_ text: String, y: Double, mine: Bool) {
            let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 48)]
            let size = (text as NSString).size(withAttributes: attrs)
            let x = mine ? w - size.width - 120 : 60
            (mine ? NSColor(calibratedRed: 0.85, green: 0.98, blue: 0.80, alpha: 1) : NSColor.white).setFill()
            NSBezierPath(roundedRect: NSRect(x: x - 30, y: y - 25, width: size.width + 60, height: size.height + 50), xRadius: 30, yRadius: 30).fill()
            (text as NSString).draw(at: NSPoint(x: x, y: y), withAttributes: attrs)
        }
        func sticker(_ emoji: String, caption: String, y: Double, mine: Bool) {
            let x = mine ? w - 520 : 60
            (emoji as NSString).draw(at: NSPoint(x: x + 60, y: y + 80), withAttributes: [.font: NSFont.systemFont(ofSize: 260)])
            (caption as NSString).draw(at: NSPoint(x: x + 90, y: y + 20), withAttributes: [.font: NSFont.boldSystemFont(ofSize: 64), .foregroundColor: NSColor.systemPurple])
        }
        bubble("are you coming tonight?", y: 2000, mine: false)
        bubble("not sure yet", y: 1850, mine: true)
        sticker("🐶", caption: "idk lol", y: 1350, mine: true)
        bubble("haha ok", y: 1150, mine: false)
        sticker("🎉", caption: "yay!", y: 600, mine: false)
        NSGraphicsContext.restoreGraphicsState()
        return rep.cgImage!
    }

    /// A group-chat capture: sender name above the sticker, timestamp below it.
    static func groupChatCapture(sender: String, sticker: String, caption: String) -> CGImage {
        let w = 280.0, h = 420.0
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(w), pixelsHigh: Int(h), bitsPerSample: 8, samplesPerPixel: 4,
                                   hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSColor(calibratedRed: 0.93, green: 0.90, blue: 0.86, alpha: 1).setFill()
        NSRect(x: 0, y: 0, width: w, height: h).fill()
        (sender as NSString).draw(at: NSPoint(x: 12, y: h - 40), withAttributes: [.font: NSFont.boldSystemFont(ofSize: 22), .foregroundColor: NSColor.systemTeal])
        (sticker as NSString).draw(at: NSPoint(x: 40, y: 110), withAttributes: [.font: NSFont.systemFont(ofSize: 170)])
        (caption as NSString).draw(at: NSPoint(x: 70, y: 70), withAttributes: [.font: NSFont.boldSystemFont(ofSize: 36), .foregroundColor: NSColor.black])
        ("15:48" as NSString).draw(at: NSPoint(x: w - 70, y: 12), withAttributes: [.font: NSFont.systemFont(ofSize: 18), .foregroundColor: NSColor.gray])
        NSGraphicsContext.restoreGraphicsState()
        return rep.cgImage!
    }

    static func tempDirectory() -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("StickerKitTests-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
}
