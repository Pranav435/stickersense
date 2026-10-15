import CoreGraphics
import CoreImage
import CoreText
import Foundation
import Vision

/// Turns a generated picture into a sticker: the subject lifted off its background, the caption
/// written in bold lettering, and a white die-cut border around both, on a transparent square.
/// The Windows app draws stickers the same way (StickerArt.cs).
public enum StickerArt {
    /// Stickers are 512 pixels square, like WhatsApp's.
    public static let size = 512
    /// The white border's width, at 512 pixels.
    static let border = 14.0
    /// A faint grey edge outside the white border, so it shows on white chat backgrounds.
    static let edge = 1.5

    // MARK: Cutting out

    /// The picture with everything but its subject made transparent (Vision's subject lifting),
    /// or nil when nothing stands out from the background.
    public static func cutOut(_ image: CGImage) -> CGImage? {
        let handler = VNImageRequestHandler(cgImage: image)
        let request = VNGenerateForegroundInstanceMaskRequest()
        do {
            try handler.perform([request])
            guard let result = request.results?.first, !result.allInstances.isEmpty else { return nil }
            let buffer = try result.generateMaskedImage(ofInstances: result.allInstances, from: handler, croppedToInstancesExtent: false)
            let ci = CIImage(cvPixelBuffer: buffer)
            return CIContext().createCGImage(ci, from: ci.extent, format: .RGBA8, colorSpace: CGColorSpace(name: CGColorSpace.sRGB))
        } catch {
            return nil
        }
    }

    // MARK: Composing

    /// Lays out `subject` (transparent around it) and `caption` on a transparent square with a
    /// white die-cut border. An opaque subject gets a border around its square.
    public static func compose(_ subject: CGImage, caption: String, size: Int = size) -> CGImage? {
        let scale = Double(size) / 512
        let border = border * scale, edge = edge * scale
        let pad = (border + edge + 4 * scale).rounded(.up)
        let box = Double(size) - 2 * pad
        guard let trimmed = trimmed(subject), let ctx = ImagePrep.rgbContext(width: size, height: size, alpha: true) else { return nil }

        // The caption sits at the bottom, overlapping the subject's lower edge a little.
        let lines = captionLines(caption, maxWidth: box, maxHeight: box * 0.34, scale: scale)
        let captionHeight = lines.map(\.height).reduce(0, +)
        let subjectBox = CGRect(x: pad, y: pad + captionHeight * 0.7, width: box, height: box - captionHeight * 0.7)
        let fit = min(subjectBox.width / Double(trimmed.width), subjectBox.height / Double(trimmed.height))
        let drawn = CGSize(width: Double(trimmed.width) * fit, height: Double(trimmed.height) * fit)
        // Context origin is bottom-left: centre horizontally, and sit on the caption (or centre without one).
        let y = lines.isEmpty ? subjectBox.midY - drawn.height / 2 : subjectBox.minY
        ctx.interpolationQuality = .high
        ctx.draw(trimmed, in: CGRect(x: subjectBox.midX - drawn.width / 2, y: y, width: drawn.width, height: drawn.height))

        var baseline = pad + captionHeight
        for line in lines {
            baseline -= line.height
            let x = (Double(size) - line.width) / 2
            ctx.textPosition = CGPoint(x: x, y: baseline + line.descent)
            ctx.setLineJoin(.round)
            ctx.setLineWidth(line.stroke)
            ctx.setStrokeColor(CGColor(gray: 1, alpha: 1))
            ctx.setTextDrawingMode(.stroke)
            CTLineDraw(line.line, ctx)
            ctx.textPosition = CGPoint(x: x, y: baseline + line.descent)
            ctx.setTextDrawingMode(.fill)
            CTLineDraw(line.line, ctx)
        }
        guard let content = ctx.makeImage() else { return nil }
        return withBorder(content, border: border, edge: edge)
    }

    /// The image cropped to its non-transparent pixels (nil if it's entirely transparent).
    static func trimmed(_ image: CGImage) -> CGImage? {
        let w = image.width, h = image.height
        guard let ctx = ImagePrep.rgbContext(width: w, height: h, alpha: true) else { return nil }
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        guard let data = ctx.data else { return nil }
        let row = ctx.bytesPerRow
        let pixels = data.bindMemory(to: UInt8.self, capacity: row * h)
        var minX = w, minY = h, maxX = -1, maxY = -1
        for y in 0..<h {
            for x in 0..<w where pixels[y * row + x * 4 + 3] > 24 {
                minX = min(minX, x); maxX = max(maxX, x)
                minY = min(minY, y); maxY = max(maxY, y)
            }
        }
        guard maxX >= minX, maxY >= minY, let full = ctx.makeImage() else { return nil }
        // Context memory and CGImage cropping are both top-down.
        return full.cropping(to: CGRect(x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1))
    }

    // MARK: Caption

    struct CaptionLine {
        var line: CTLine
        var width: Double
        var height: Double
        var descent: Double
        var stroke: Double
    }

    /// The caption in one line, or two when one would be too small, shrunk to fit.
    static func captionLines(_ caption: String, maxWidth: Double, maxHeight: Double, scale: Double) -> [CaptionLine] {
        let text = caption.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return [] }
        let words = text.split(separator: " ").map(String.init)
        var splits: [[String]] = [[text]]
        if words.count > 1 {
            // Every way of breaking into two lines, most balanced first.
            splits += (1..<words.count)
                .map { [words[..<$0].joined(separator: " "), words[$0...].joined(separator: " ")] }
                .sorted { max($0[0].count, $0[1].count) < max($1[0].count, $1[1].count) }
        }
        let largest = 92 * scale, smallest = 34 * scale
        var best: [CaptionLine] = []
        var bestSize = 0.0
        for parts in splits.prefix(3) {
            var fontSize = largest
            while fontSize >= smallest {
                let lines = parts.map { make($0, fontSize: fontSize) }
                if lines.allSatisfy({ $0.width <= maxWidth }), lines.map(\.height).reduce(0, +) <= maxHeight { break }
                fontSize -= 2 * scale
            }
            // One line wins unless two lines are clearly bigger.
            if fontSize > bestSize * (best.count == 1 ? 1.25 : 1) {
                bestSize = fontSize
                best = parts.map { make($0, fontSize: max(fontSize, smallest)) }
            }
        }
        return best
    }

    private static func make(_ text: String, fontSize: Double) -> CaptionLine {
        let font = CTFontCreateWithName("AvenirNext-Heavy" as CFString, fontSize, nil)
        let attributes: [CFString: Any] = [
            kCTFontAttributeName: font,
            kCTForegroundColorAttributeName: CGColor(red: 0.1, green: 0.1, blue: 0.12, alpha: 1),
        ]
        let line = CTLineCreateWithAttributedString(CFAttributedStringCreate(nil, text as CFString, attributes as CFDictionary))
        var ascent: CGFloat = 0, descent: CGFloat = 0, leading: CGFloat = 0
        let width = CTLineGetTypographicBounds(line, &ascent, &descent, &leading)
        let stroke = fontSize * 0.22
        return CaptionLine(line: line, width: width + stroke, height: ascent + descent + stroke * 0.5, descent: descent + stroke * 0.25, stroke: stroke)
    }

    // MARK: Border

    /// Puts a white border `border` wide (and a faint grey edge) around everything visible.
    static func withBorder(_ content: CGImage, border: Double, edge: Double) -> CGImage? {
        let w = content.width, h = content.height
        guard let ctx = ImagePrep.rgbContext(width: w, height: h, alpha: true) else { return nil }
        ctx.draw(content, in: CGRect(x: 0, y: 0, width: w, height: h))
        guard let data = ctx.data else { return nil }
        let row = ctx.bytesPerRow
        let pixels = data.bindMemory(to: UInt8.self, capacity: row * h)
        var inside = [Bool](repeating: false, count: w * h)
        for y in 0..<h { for x in 0..<w { inside[y * w + x] = pixels[y * row + x * 4 + 3] >= 128 } }
        let distance = distanceToInside(inside, width: w, height: h)
        for y in 0..<h {
            for x in 0..<w {
                let d = distance[y * w + x]
                // Under the content: white within the border, then the grey edge, anti-aliased.
                let white = min(max(border + 0.5 - d, 0), 1)
                let outer = min(max(border + edge + 0.5 - d, 0), 1)
                let backAlpha = max(white, outer * 0.22)
                guard backAlpha > 0 else { continue }
                let grey = white >= outer * 0.22 ? 1.0 : 0.35
                let backValue = grey * backAlpha // premultiplied
                let i = y * row + x * 4
                let a = Double(pixels[i + 3]) / 255
                // Content (premultiplied) over the backing.
                for c in 0..<3 { pixels[i + c] = UInt8(min(255, (Double(pixels[i + c]) + backValue * 255 * (1 - a)).rounded())) }
                pixels[i + 3] = UInt8(min(255, ((a + backAlpha * (1 - a)) * 255).rounded()))
            }
        }
        return ctx.makeImage()
    }

    /// Euclidean distance from every pixel to the nearest `inside` pixel (0 inside), by
    /// Felzenszwalb and Huttenlocher's two-pass squared distance transform.
    static func distanceToInside(_ inside: [Bool], width w: Int, height h: Int) -> [Double] {
        let infinity = Double(w * w + h * h)
        var grid = inside.map { $0 ? 0 : infinity }
        var line = [Double](repeating: 0, count: max(w, h))
        var out = [Double](repeating: 0, count: max(w, h))
        for x in 0..<w {
            for y in 0..<h { line[y] = grid[y * w + x] }
            transform1D(line, count: h, into: &out)
            for y in 0..<h { grid[y * w + x] = out[y] }
        }
        for y in 0..<h {
            for x in 0..<w { line[x] = grid[y * w + x] }
            transform1D(line, count: w, into: &out)
            for x in 0..<w { grid[y * w + x] = out[x].squareRoot() }
        }
        return grid
    }

    private static func transform1D(_ f: [Double], count n: Int, into d: inout [Double]) {
        var v = [Int](repeating: 0, count: n)
        var z = [Double](repeating: 0, count: n + 1)
        var k = 0
        z[0] = -.infinity
        z[1] = .infinity
        for q in 1..<max(n, 1) {
            var s = ((f[q] + Double(q * q)) - (f[v[k]] + Double(v[k] * v[k]))) / Double(2 * q - 2 * v[k])
            while s <= z[k] {
                k -= 1
                s = ((f[q] + Double(q * q)) - (f[v[k]] + Double(v[k] * v[k]))) / Double(2 * q - 2 * v[k])
            }
            k += 1
            v[k] = q
            z[k] = s
            z[k + 1] = .infinity
        }
        k = 0
        for q in 0..<n {
            while z[k + 1] < Double(q) { k += 1 }
            let dq = Double(q - v[k])
            d[q] = dq * dq + f[v[k]]
        }
    }
}
