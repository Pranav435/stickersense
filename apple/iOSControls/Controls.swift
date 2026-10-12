import AppIntents
import SwiftUI
import WidgetKit

/// Control Center controls (and Lock Screen / Action button) for StickerSense.
@main
struct StickerSenseControls: WidgetBundle {
    var body: some Widget {
        DescribeCopiedControl()
        StickerPickerControl()
    }
}

struct DescribeCopiedControl: ControlWidget {
    var body: some ControlWidgetConfiguration {
        StaticControlConfiguration(kind: "com.pranavsavla.StickerSense.describeCopied") {
            ControlWidgetButton(action: DescribeCopiedStickerIntent()) {
                Label("Describe Copied Sticker", systemImage: "text.below.photo")
            }
        }
        .displayName("Describe Copied Sticker")
        .description("Describes the sticker, GIF or picture you copied.")
    }
}

struct StickerPickerControl: ControlWidget {
    var body: some ControlWidgetConfiguration {
        StaticControlConfiguration(kind: "com.pranavsavla.StickerSense.picker") {
            ControlWidgetButton(action: OpenStickerPickerIntent()) {
                Label("Sticker Picker", systemImage: "face.smiling")
            }
        }
        .displayName("Sticker Picker")
        .description("Find a sticker or GIF to send.")
    }
}
