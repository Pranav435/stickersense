# StickerSense for Mac, iPhone and iPad

## Mac

### Requirements

- macOS 27 or later on Apple silicon, with Apple Intelligence turned on (System Settings, Apple Intelligence & Siri).
- VoiceOver. StickerSense also works without it, following keyboard focus and speaking with the system voice, but it's designed around VoiceOver.

### Install

Install with Homebrew:

```sh
brew install --cask pranav435/tap/stickersense
```

Or download `StickerSense.zip` from the [latest release](https://github.com/Pranav435/stickersense/releases/latest), or build it yourself (see Building). StickerSense isn't notarized yet, so the first launch needs one extra step: open it once, then go to System Settings › Privacy & Security and choose Open Anyway.

Open StickerSense like any app, for example from Spotlight, and its main window appears. The window shows what StickerSense is doing, has buttons for the picker, history, settings and setup, and lists every shortcut. While any StickerSense window is open, StickerSense is in Command-Tab and the Dock, with its own menu bar. When you close its windows, it keeps running quietly in the menu bar (press VO-M twice to reach it), so stickers keep being described. When it starts at login, it stays in the menu bar without opening a window.

The first time it opens, the Setup window walks you through everything. Setup only reappears if something is missing.

1. **VoiceOver AppleScript control.** In VoiceOver Utility, General, turn on "Allow VoiceOver to be controlled with AppleScript". When macOS asks whether StickerSense may control VoiceOver, choose Allow.
2. **Screen Recording,** so StickerSense can see the sticker and its animation. macOS may ask you to reopen StickerSense afterwards.
3. **Accessibility,** so the picker can paste into your chat.
4. **WhatsApp stickers** (optional). Press Import WhatsApp Stickers. An Open dialog appears already inside WhatsApp's folder; just press Return. Choosing it yourself is what gives StickerSense permission, because macOS doesn't ask for this kind of access on its own. Imported stickers are described in the background, but only after 90 seconds with no keyboard or trackpad use, so the model never competes with VoiceOver while you work.
5. **Practise** on the practice sticker at the bottom of Setup.

### Shortcuts

All shortcuts are Option Shift Command plus a letter, and you can change the letters in Settings. You can also hear them any time: choose "What Can StickerSense Do?" in the menu bar menu.

- **Option Shift Command D:** describe what the VoiceOver cursor is on. Press it again within 3 seconds for the detailed description, and a third time to hear the detail again. After an automatic description, one press gives the detail.
- **Option Shift Command S:** sticker picker. Type what you want. Your own stickers are read out straight away ("3 of yours and 50 from GIPHY. First: …"); if GIPHY is slow, you hear "Plus 50 from GIPHY" when they arrive. Press Return to paste the first result into the chat you came from, or Tab to the list and use the arrow keys. GIPHY results come 50 at a time, and more load as you near the end of the list; the last row, "More GIPHY results", loads the next 50 and moves you to them. GIPHY results are described on this Mac as you reach them, and those descriptions are remembered, so popular GIFs are instant next time. Each result has actions in the VoiceOver rotor: paste, describe in detail, copy, and remove. Escape closes the picker.
- **Type two colons (::) in a chat:** also opens the sticker picker, like emoji pickers in Slack and Discord. The colons are replaced by the sticker you pick; press Escape and they stay as you typed them. This only works in chat apps and browsers, in the message box, and can be turned off in Settings.
- **Option Shift Command H:** history. Return on an entry speaks its detailed description.
- **Option Shift Command A:** turn automatic descriptions on or off.

### Sounds

- A soft blip means it's working. You won't hear it when the answer comes straight from memory.
- A short two-note chime plays just before the description.
- A low tone means something went wrong, followed by a spoken explanation.
- If a description takes a while, the blip repeats every couple of seconds and you'll hear "Still working" once.

You can turn the sounds off or change their volume in Settings.

### Troubleshooting

- **It plays the chime but says nothing.** Open Settings and make sure "Send descriptions to VoiceOver with" is set to Automatic. VoiceOver ignores announcements from apps in the background, so StickerSense speaks through VoiceOver's AppleScript output instead. The activity log, in Settings under Show Activity Log, says "Spoken via VoiceOver output ✓" when VoiceOver confirmed it spoke.
- **"VoiceOver doesn't allow AppleScript control."** Turn on the checkbox in VoiceOver Utility, General.
- **"The VoiceOver cursor is on a large area."** The cursor is on a whole list or window. Move it onto the sticker itself; in WhatsApp, interact with the message list first.
- **A sticker isn't described automatically.** Auto mode reacts when VoiceOver's description of the item mentions a sticker or GIF. The shortcut works on anything. The log shows what VoiceOver said ("Auto: …"), which helps add new apps.
- **macOS says the app is damaged or offers to move it to the Trash.** It was signed with a revoked certificate. Rebuild with a valid one; the build script refuses revoked certificates.

## iPhone and iPad

iOS doesn't let apps see the screen or follow the VoiceOver cursor, so StickerSense works through Shortcuts, the share sheet and Control Center.

- **Back Tap (or Action button): describe the stickers on screen.**
  1. In the Shortcuts app, make a new shortcut.
  2. Add the action Take Screenshot, then the action Describe Stickers from StickerSense.
  3. In Settings, go to Accessibility, Touch, Back Tap, Double Tap, and choose the shortcut.

  Now double-tap the back of the phone in any chat. You hear the stickers on screen, newest first: "Newest: party hat with confetti, “yay!”. Before that: dog with tongue out, “idk lol”."
- **Describe Copied Sticker.** Copy a sticker or GIF in your chat app, then use the Control Center control (Edit, Add a Control, StickerSense) or Siri ("Describe copied sticker with StickerSense").
- **Share sheet.** Share any sticker, GIF or picture to StickerSense.
- **Stickers tab.** The picker: search, then double-tap to copy and paste it into your chat. Actions include More detail and Remove.

The app's Set Up tab repeats these steps.

## Building

Requirements: Xcode 27, XcodeGen (`brew install xcodegen`).

- **Mac app.** Run `STICKERSENSE_SIGN_IDENTITY="<your Apple Development certificate SHA-1>" apple/scripts/build-mac.sh`. It copies the sources to `~/Library/Caches/StickerSense-build` (an iCloud-synced Documents folder can stall `xcodebuild`), builds, signs with your certificate so macOS keeps its permissions across rebuilds, installs to `~/Applications` and launches it. It refuses revoked certificates.
- **iPhone app.** Run `xcodegen generate` in `apple/`, open `StickerSense.xcodeproj`, set your team on the three iOS targets (the App Group is `group.com.pranavsavla.StickerSense`), and run the StickerSenseiOS scheme.
- **Tests.** In `apple/StickerKit`, run `swift test`. Add `STICKERSENSE_MODEL_TESTS=1` to also run the tests that use the real on-device model. If the folder is synced with iCloud, add `--scratch-path` with a folder outside it.
