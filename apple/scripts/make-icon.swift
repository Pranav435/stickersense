// Draws StickerSense's app icon (a smiling sticker with sound waves) into the Mac asset catalog.
// Run: swift apple/scripts/make-icon.swift <path-to-AppIcon.appiconset>
import AppKit

let out = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

func render(_ px: Int) -> Data {
    let s = CGFloat(px)
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let inset = s * 0.1
    let tile = NSRect(x: inset, y: inset, width: s - 2 * inset, height: s - 2 * inset)
    let path = NSBezierPath(roundedRect: tile, xRadius: tile.width * 0.225, yRadius: tile.width * 0.225)
    NSGradient(starting: NSColor(calibratedRed: 0.46, green: 0.27, blue: 0.93, alpha: 1),
               ending: NSColor(calibratedRed: 0.10, green: 0.72, blue: 0.75, alpha: 1))!.draw(in: path, angle: -60)
    let config = NSImage.SymbolConfiguration(pointSize: s * 0.42, weight: .semibold)
        .applying(.init(paletteColors: [.white]))
    if let face = NSImage(systemSymbolName: "face.smiling.inverse", accessibilityDescription: nil)?.withSymbolConfiguration(config) {
        let size = face.size
        face.draw(in: NSRect(x: tile.midX - size.width / 2 - s * 0.06, y: tile.midY - size.height / 2, width: size.width, height: size.height))
    }
    let waveConfig = NSImage.SymbolConfiguration(pointSize: s * 0.2, weight: .bold).applying(.init(paletteColors: [.white]))
    if let waves = NSImage(systemSymbolName: "wave.3.right", accessibilityDescription: nil)?.withSymbolConfiguration(waveConfig) {
        let size = waves.size
        waves.draw(in: NSRect(x: tile.maxX - size.width - s * 0.07, y: tile.midY - size.height / 2, width: size.width, height: size.height))
    }
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

var images: [[String: String]] = []
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let name = "icon_\(points)x\(points)\(scale == 2 ? "@2x" : "").png"
        try render(points * scale).write(to: out.appendingPathComponent(name))
        images.append(["idiom": "mac", "size": "\(points)x\(points)", "scale": "\(scale)x", "filename": name])
    }
}
let contents: [String: Any] = ["images": images, "info": ["author": "xcode", "version": 1]]
try JSONSerialization.data(withJSONObject: contents, options: [.prettyPrinted, .sortedKeys]).write(to: out.appendingPathComponent("Contents.json"))
let catalog = out.deletingLastPathComponent().appendingPathComponent("Contents.json")
try #"{"info":{"author":"xcode","version":1}}"#.write(to: catalog, atomically: true, encoding: .utf8)
print("Wrote \(images.count) icon sizes to \(out.path)")
