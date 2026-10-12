import CoreGraphics
import Foundation

/// Identifies "the same sticker" across screenshots, even when it is shown at a different
/// size or position. The image is first cropped to its content (dropping the chat or
/// transparent background around it), then summarised two ways: a 256-bit gradient hash
/// (shape) and a 16×16 colour thumbnail (detail and colour).
public struct Fingerprint: Codable, Sendable, Hashable {
    /// 16×16 difference hash, packed into four 64-bit words.
    public var dHash: [UInt64]
    public var thumb: Thumbprint

    public init(dHash: [UInt64], thumb: Thumbprint) {
        self.dHash = dHash
        self.thumb = thumb
    }

    public init(_ image: CGImage) {
        let content = ContentCrop.crop(ImagePrep.flattened(image))
        dHash = Self.differenceHash(content)
        thumb = Thumbprint(content)
    }

    public func hammingDistance(to other: Fingerprint) -> Int {
        guard dHash.count == other.dHash.count else { return Int.max }
        return zip(dHash, other.dHash).reduce(0) { $0 + ($1.0 ^ $1.1).nonzeroBitCount }
    }

    /// Strict on purpose: speaking the wrong cached description is far worse than a 1-second wait.
    public func isSameSticker(as other: Fingerprint) -> Bool {
        hammingDistance(to: other) <= 10 && thumb.difference(to: other.thumb) <= 0.0065
    }

    static func differenceHash(_ image: CGImage) -> [UInt64] {
        let w = 17, h = 16
        let gray = grayscale(image, width: w, height: h)
        var words = [UInt64](repeating: 0, count: 4)
        var bit = 0
        for y in 0..<h {
            for x in 0..<(w - 1) {
                if gray[y * w + x] > gray[y * w + x + 1] {
                    words[bit / 64] |= (1 << UInt64(bit % 64))
                }
                bit += 1
            }
        }
        return words
    }

    static func grayscale(_ image: CGImage, width: Int, height: Int) -> [UInt8] {
        var pixels = [UInt8](repeating: 255, count: width * height)
        let flat = ImagePrep.flattened(image)
        pixels.withUnsafeMutableBytes { raw in
            guard let ctx = CGContext(
                data: raw.baseAddress, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width,
                space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue
            ) else { return }
            ctx.interpolationQuality = .medium
            ctx.draw(flat, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
        return pixels
    }
}

/// A small colour thumbnail used to compare images.
public struct Thumbprint: Codable, Sendable, Hashable {
    public static let side = 16
    /// RGB bytes, row-major, `side * side * 3` long.
    public var rgb: Data

    public init(rgb: Data) { self.rgb = rgb }

    public init(_ image: CGImage, side: Int = Thumbprint.side) {
        var buffer = [UInt8](repeating: 255, count: side * side * 4)
        let flat = ImagePrep.flattened(image)
        buffer.withUnsafeMutableBytes { raw in
            guard let ctx = CGContext(
                data: raw.baseAddress, width: side, height: side, bitsPerComponent: 8, bytesPerRow: side * 4,
                space: CGColorSpace(name: CGColorSpace.sRGB)!,
                bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
            ) else { return }
            ctx.interpolationQuality = .high
            ctx.draw(flat, in: CGRect(x: 0, y: 0, width: side, height: side))
        }
        var rgb = Data(capacity: side * side * 3)
        for i in stride(from: 0, to: buffer.count, by: 4) {
            rgb.append(buffer[i]); rgb.append(buffer[i + 1]); rgb.append(buffer[i + 2])
        }
        self.rgb = rgb
    }

    /// Mean absolute difference in 0…1.
    public func difference(to other: Thumbprint) -> Double {
        guard rgb.count == other.rgb.count, !rgb.isEmpty else { return 1 }
        var total = 0
        for i in rgb.indices { total += abs(Int(rgb[i]) - Int(other.rgb[i])) }
        return Double(total) / Double(rgb.count * 255)
    }
}

/// Trims the uniform background around a sticker so position and padding don't matter.
enum ContentCrop {
    static func crop(_ image: CGImage) -> CGImage {
        let side = 128
        let scaleX = Double(image.width) / Double(side), scaleY = Double(image.height) / Double(side)
        var px = [UInt8](repeating: 0, count: side * side * 4)
        px.withUnsafeMutableBytes { raw in
            guard let ctx = CGContext(
                data: raw.baseAddress, width: side, height: side, bitsPerComponent: 8, bytesPerRow: side * 4,
                space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
            ) else { return }
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: side, height: side))
        }
        func color(_ x: Int, _ y: Int) -> (Int, Int, Int) {
            let i = (y * side + x) * 4
            return (Int(px[i]), Int(px[i + 1]), Int(px[i + 2]))
        }
        // Background = the colour the four corners agree on (if they do).
        let corners = [color(0, 0), color(side - 1, 0), color(0, side - 1), color(side - 1, side - 1)]
        let bg = corners[0]
        func distance(_ a: (Int, Int, Int), _ b: (Int, Int, Int)) -> Int { abs(a.0 - b.0) + abs(a.1 - b.1) + abs(a.2 - b.2) }
        guard corners.allSatisfy({ distance($0, bg) < 24 }) else { return image }

        var minX = side, minY = side, maxX = -1, maxY = -1
        for y in 0..<side {
            for x in 0..<side where distance(color(x, y), bg) > 36 {
                minX = min(minX, x); maxX = max(maxX, x)
                minY = min(minY, y); maxY = max(maxY, y)
            }
        }
        guard maxX >= minX, maxY >= minY, (maxX - minX) >= 8, (maxY - minY) >= 8 else { return image }
        // Context rows run bottom-up; CGImage.cropping is top-down.
        let rect = CGRect(
            x: Double(minX) * scaleX,
            y: Double(side - 1 - maxY) * scaleY,
            width: Double(maxX - minX + 1) * scaleX,
            height: Double(maxY - minY + 1) * scaleY
        ).integral.intersection(CGRect(x: 0, y: 0, width: image.width, height: image.height))
        return image.cropping(to: rect) ?? image
    }
}

/// Detects motion between two screen captures of the same region.
public struct MotionSignature: Sendable {
    static let side = 48
    let gray: [UInt8]

    public init(_ image: CGImage) {
        gray = Fingerprint.grayscale(image, width: Self.side, height: Self.side)
    }

    /// Fraction of pixels that changed noticeably (0…1).
    public func changedFraction(comparedTo other: MotionSignature) -> Double {
        guard gray.count == other.gray.count, !gray.isEmpty else { return 1 }
        var changed = 0
        for i in gray.indices where abs(Int(gray[i]) - Int(other.gray[i])) > 24 { changed += 1 }
        return Double(changed) / Double(gray.count)
    }

    /// True when the region visibly moved — e.g. an animated sticker or GIF.
    public func differs(from other: MotionSignature) -> Bool {
        changedFraction(comparedTo: other) > 0.004
    }
}
