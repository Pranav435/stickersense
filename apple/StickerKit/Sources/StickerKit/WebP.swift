import CoreGraphics
import Foundation
import libwebp

public extension ImagePrep {
    /// A WebP of the image with its transparency, as WhatsApp wants stickers: at most `maxBytes`
    /// (WhatsApp's limit for a still sticker is 100 KB), trying lower qualities until it fits.
    static func webpData(_ image: CGImage, maxBytes: Int = 100_000) throws -> Data {
        let w = image.width, h = image.height
        guard let ctx = rgbContext(width: w, height: h, alpha: true) else { throw ImagePrepError.encodingFailed }
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        guard let data = ctx.data else { throw ImagePrepError.encodingFailed }
        let row = ctx.bytesPerRow
        // CoreGraphics premultiplies alpha; WebP wants it straight.
        var rgba = [UInt8](repeating: 0, count: w * h * 4)
        let pixels = data.bindMemory(to: UInt8.self, capacity: row * h)
        for y in 0..<h {
            for x in 0..<w {
                let i = y * row + x * 4, o = (y * w + x) * 4
                let a = pixels[i + 3]
                rgba[o + 3] = a
                guard a > 0 else { continue }
                for c in 0..<3 { rgba[o + c] = UInt8(min(255, (Int(pixels[i + c]) * 255 + Int(a) / 2) / Int(a))) }
            }
        }
        for quality: Float in [85, 72, 60, 45, 30] {
            var output: UnsafeMutablePointer<UInt8>?
            let size = WebPEncodeRGBA(rgba, Int32(w), Int32(h), Int32(w * 4), quality, &output)
            defer { WebPFree(output) }
            if size > 0, size <= maxBytes, let output { return Data(bytes: output, count: size) }
        }
        throw ImagePrepError.encodingFailed
    }
}
