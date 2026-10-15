// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "StickerKit",
    platforms: [.macOS("27.0"), .iOS("27.0")],
    products: [
        .library(name: "StickerKit", targets: ["StickerKit"]),
    ],
    dependencies: [
        // Google's libwebp (BSD): WhatsApp stickers are WebP, and ImageIO can't write it.
        .package(url: "https://github.com/SDWebImage/libwebp-Xcode", exact: "1.6.0"),
    ],
    targets: [
        .target(name: "StickerKit", dependencies: [.product(name: "libwebp", package: "libwebp-Xcode")]),
        .testTarget(name: "StickerKitTests", dependencies: ["StickerKit"]),
    ]
)
