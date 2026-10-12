import StickerKit
import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// Share extension: share a sticker, GIF or picture from any app to hear it described.
final class ShareViewController: UIViewController {
    override func viewDidLoad() {
        super.viewDidLoad()
        TextReader.warmUp()
        Task { await load() }
    }

    private func load() async {
        let data = await sharedImageData()
        let view = DescribeView(imageData: data) { [weak self] in
            self?.extensionContext?.completeRequest(returningItems: nil)
        }
        let host = UIHostingController(rootView: view)
        addChild(host)
        host.view.frame = self.view.bounds
        host.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        self.view.addSubview(host.view)
        host.didMove(toParent: self)
    }

    /// The first shared image, as raw data so GIF/WebP animation survives.
    private func sharedImageData() async -> Data? {
        let providers = (extensionContext?.inputItems as? [NSExtensionItem] ?? []).flatMap { $0.attachments ?? [] }
        let preferred = [UTType.gif, UTType.webP, UTType.png, UTType.jpeg, UTType.heic, UTType.image]
        for provider in providers {
            for type in preferred where provider.hasItemConformingToTypeIdentifier(type.identifier) {
                if let data = try? await provider.loadDataRepresentation(for: type) { return data }
            }
        }
        return nil
    }
}

private extension NSItemProvider {
    func loadDataRepresentation(for type: UTType) async throws -> Data {
        try await withCheckedThrowingContinuation { continuation in
            _ = loadDataRepresentation(forTypeIdentifier: type.identifier) { data, error in
                if let data { continuation.resume(returning: data) } else { continuation.resume(throwing: error ?? CocoaError(.fileReadUnknown)) }
            }
        }
    }
}
