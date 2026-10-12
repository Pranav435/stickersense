// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "StickerKit",
    platforms: [.macOS("27.0"), .iOS("27.0")],
    products: [
        .library(name: "StickerKit", targets: ["StickerKit"]),
    ],
    targets: [
        .target(name: "StickerKit"),
        .testTarget(name: "StickerKitTests", dependencies: ["StickerKit"]),
    ]
)
