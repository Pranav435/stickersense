import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

public enum ImagePrepError: Error, Sendable {
    case unreadableImage
    case encodingFailed
}

/// Image utilities shared by every platform: flattening transparency, scaling,
/// pulling frames out of animated stickers and writing images back out.
public enum ImagePrep {
    /// Stickers are usually transparent. Models read transparent pixels as black,
    /// so composite onto white (what most chat apps show behind stickers anyway).
    public static func flattened(_ image: CGImage, background: CGColor = CGColor(gray: 1, alpha: 1)) -> CGImage {
        switch image.alphaInfo {
        case .none, .noneSkipFirst, .noneSkipLast:
            return image
        default:
            break
        }
        guard let ctx = rgbContext(width: image.width, height: image.height) else { return image }
        let rect = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        ctx.setFillColor(background)
        ctx.fill(rect)
        ctx.draw(image, in: rect)
        return ctx.makeImage() ?? image
    }

    /// Scales down so the longest side is at most `maxDimension` pixels. Never scales up.
    public static func downscaled(_ image: CGImage, maxDimension: Int) -> CGImage {
        let longest = max(image.width, image.height)
        guard longest > maxDimension, longest > 0 else { return image }
        let scale = Double(maxDimension) / Double(longest)
        let w = max(1, Int((Double(image.width) * scale).rounded()))
        let h = max(1, Int((Double(image.height) * scale).rounded()))
        guard let ctx = rgbContext(width: w, height: h, alpha: true) else { return image }
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage() ?? image
    }

    /// Everything the describer needs: flattened, reasonably sized.
    public static func prepareForModel(_ image: CGImage) -> CGImage {
        downscaled(flattened(image), maxDimension: 768)
    }

    /// Paints over regions (normalised, origin bottom-left — Vision's convention) with the colour
    /// around them. Used to hide the sender's name and the time inside a chat capture.
    public static func masking(_ image: CGImage, boxes: [CGRect]) -> CGImage {
        guard !boxes.isEmpty else { return image }
        let w = image.width, h = image.height
        guard let ctx = rgbContext(width: w, height: h, alpha: true) else { return image }
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        guard let data = ctx.data else { return image }
        let bytesPerRow = ctx.bytesPerRow
        let pixels = data.bindMemory(to: UInt8.self, capacity: bytesPerRow * h)
        // Context memory is top-down; Vision boxes are bottom-up.
        func colour(atX x: Int, y: Int) -> (Double, Double, Double) {
            let cx = min(max(x, 0), w - 1), cy = min(max(h - 1 - y, 0), h - 1)
            let i = cy * bytesPerRow + cx * 4
            return (Double(pixels[i]), Double(pixels[i + 1]), Double(pixels[i + 2]))
        }
        for box in boxes {
            let rect = CGRect(x: box.minX * Double(w), y: box.minY * Double(h), width: box.width * Double(w), height: box.height * Double(h))
                .insetBy(dx: -3, dy: -3).integral
            // Average the colour just outside the box's four corners.
            let samples = [colour(atX: Int(rect.minX) - 1, y: Int(rect.minY) - 1), colour(atX: Int(rect.maxX) + 1, y: Int(rect.minY) - 1),
                           colour(atX: Int(rect.minX) - 1, y: Int(rect.maxY) + 1), colour(atX: Int(rect.maxX) + 1, y: Int(rect.maxY) + 1)]
            let r = samples.map(\.0).reduce(0, +) / 4, g = samples.map(\.1).reduce(0, +) / 4, b = samples.map(\.2).reduce(0, +) / 4
            ctx.setFillColor(CGColor(srgbRed: r / 255, green: g / 255, blue: b / 255, alpha: 1))
            ctx.fill(rect)
        }
        return ctx.makeImage() ?? image
    }

    /// True for a capture with nothing in it — one flat colour. That happens when the screen
    /// region is blank or protected; describing it would only say "black rectangle".
    public static func isBlank(_ image: CGImage) -> Bool {
        Thumbprint(image, side: 24).isUniform
    }

    // MARK: Animated images

    /// Loads an image file (PNG, JPEG, GIF, WebP, APNG, HEIC…) and returns up to
    /// `maxFrames` frames spread evenly across the animation. Static images return one frame.
    public static func frames(from url: URL, maxFrames: Int = 4) throws -> [CGImage] {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { throw ImagePrepError.unreadableImage }
        return try frames(from: source, maxFrames: maxFrames)
    }

    public static func frames(from data: Data, maxFrames: Int = 4) throws -> [CGImage] {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { throw ImagePrepError.unreadableImage }
        return try frames(from: source, maxFrames: maxFrames)
    }

    static func frames(from source: CGImageSource, maxFrames: Int) throws -> [CGImage] {
        let count = CGImageSourceGetCount(source)
        guard count > 0 else { throw ImagePrepError.unreadableImage }
        let indices = sampleIndices(count: count, max: maxFrames)
        let options = [kCGImageSourceShouldCacheImmediately: true] as CFDictionary
        let images = indices.compactMap { CGImageSourceCreateImageAtIndex(source, $0, options) }
        guard !images.isEmpty else { throw ImagePrepError.unreadableImage }
        return images
    }

    /// Evenly spread frame indices, always including the first frame.
    public static func sampleIndices(count: Int, max: Int) -> [Int] {
        guard count > 0, max > 0 else { return [] }
        guard count > max else { return Array(0..<count) }
        if max == 1 { return [0] }
        return (0..<max).map { i in Int((Double(i) * Double(count - 1) / Double(max - 1)).rounded()) }
    }

    /// Drops frames that look the same as the frame before them, so a sticker that
    /// only animates briefly doesn't waste the model's attention on duplicates.
    public static func distinctFrames(_ frames: [CGImage]) -> [CGImage] {
        guard let first = frames.first else { return [] }
        var result = [first]
        var last = MotionSignature(first)
        for frame in frames.dropFirst() {
            let signature = MotionSignature(frame)
            if signature.differs(from: last) {
                result.append(frame)
                last = signature
            }
        }
        return result
    }

    // MARK: Encoding

    public static func pngData(_ image: CGImage) throws -> Data {
        try encode([image], type: .png, frameDelay: nil)
    }

    /// Writes an animated GIF (or a single-frame GIF).
    public static func gifData(_ frames: [CGImage], frameDelay: Double = 0.25) throws -> Data {
        try encode(frames, type: .gif, frameDelay: frameDelay)
    }

    static func encode(_ frames: [CGImage], type: UTType, frameDelay: Double?) throws -> Data {
        let data = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(data, type.identifier as CFString, frames.count, nil) else {
            throw ImagePrepError.encodingFailed
        }
        if type == .gif {
            let fileProps = [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]] as CFDictionary
            CGImageDestinationSetProperties(dest, fileProps)
        }
        for frame in frames {
            var props: CFDictionary?
            if type == .gif, let frameDelay {
                props = [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: frameDelay]] as CFDictionary
            }
            CGImageDestinationAddImage(dest, frame, props)
        }
        guard CGImageDestinationFinalize(dest) else { throw ImagePrepError.encodingFailed }
        return data as Data
    }

    static func rgbContext(width: Int, height: Int, alpha: Bool = false) -> CGContext? {
        CGContext(
            data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: alpha ? CGImageAlphaInfo.premultipliedLast.rawValue : CGImageAlphaInfo.noneSkipLast.rawValue
        )
    }
}
