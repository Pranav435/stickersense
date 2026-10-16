# StickerSense

> **A friend:** *sends a sticker*
>
> **Your screen reader:** "Sticker."
>
> **StickerSense:** "Dog with tongue out and wide eyes, 'idk lol'."

That last line is a real description from StickerSense's tests.

StickerSense says what's in the stickers, GIFs and pictures people send you, out loud, in any app. It's made for people who use a screen reader: VoiceOver on a Mac, and NVDA, JAWS or Narrator on Windows. It runs on your own computer, quickly enough that you hear the sticker before the conversation has moved on.

It works the other way round too. Search for a sticker to send, and if the right one doesn't exist, StickerSense draws it for you.

It's free and open source, with no account, ads or tracking.

## What you'll hear

- **The sticker you're on.** Put your screen reader on a sticker and press Option Shift Command D on a Mac, or Control Alt Shift D on Windows, for a short description. Press it again within 3 seconds for the long version: colours, art style, what moves, and what people mean when they send it.
- **Stickers as you reach them.** In WhatsApp, Messages, Telegram, Discord, Signal, Slack, Messenger and chat websites, a sticker or GIF is described as soon as you land on it, right after your screen reader's own announcement. Come back to it later and you hear it again, instantly. No keypress needed.
- **What happens in animations.** StickerSense looks at a few frames and tells you the plot: "Yellow cat, eyes open, then become hearts while mouth opens." Animated stickers no longer just "move" at you.
- **The exact words.** On-device text recognition reads the words on a sticker, and StickerSense only reports words that are really there. The language model is a fine describer but a creative reader; it once insisted a dumpling said "red lips, green eyes". It no longer gets a vote on what the text says.
- **The sticker, not the sender.** In group chats, the sender's name and the time are painted out before describing, so "Ana" is never read as the sticker's caption.
- **Repeats, instantly.** Everything StickerSense describes is remembered. The second time a sticker turns up, from anyone, there's no wait.
- **Your history.** Every description is kept, with the long version one keypress away.

## Sending stickers

Type two colons (::) in a chat, or press Option Shift Command S on a Mac (Control Alt Shift S on Windows), to open the sticker picker. Search by what a sticker shows, says or means: "birthday", "lol", "thank you". It looks through the stickers you've heard, your WhatsApp stickers, and GIPHY, 50 at a time. Pick one and it's pasted into your chat in place of the colons. Pressing Send is still up to you.

### Making your own

When the search comes up short, press Command G (Control G on Windows), or the Generate sticker button that appears once you've typed something. StickerSense makes 4 stickers of what you typed, on your own computer: a cartoon character, cut out, with a white die-cut border and your words written on it. Image models can't spell, so StickerSense writes the words itself.

Each one is described as soon as it's ready, so you know which corgi is bowing politely and which one is holding something nobody asked for. Only the stickers you send are kept, under Made by you.

All 4 take about 15 seconds on an Apple silicon Mac. On a Windows PC without a graphics card, the first is ready after about 45 seconds and all 4 after about 2 minutes. The first time, StickerSense asks before downloading the sticker model: 900 MB on a Mac, 1.1 GB on Windows.

### In WhatsApp

WhatsApp turns any picture you paste into a photo, so a pasted sticker arrives as a big square with a white background. On a Mac, StickerSense goes around that: still stickers are made into real stickers with WhatsApp's own sticker maker and sent straight away, as if you'd tapped one in WhatsApp's sticker tray. It needs WhatsApp in English. On Windows, WhatsApp still gets a photo for now.

## Install

### Mac

```sh
brew install --cask pranav435/tap/stickersense
```

That's [Homebrew](https://brew.sh); `brew upgrade` fetches each new version. You can also download `StickerSense-Mac.zip` from [Releases](https://github.com/Pranav435/stickersense/releases/latest), unzip it, and move StickerSense to Applications.

macOS refuses to open StickerSense the first time, because it isn't notarized. Notarization is Apple's $99-a-year stamp of approval, and [Donate](#donate) is saving up for it. Until then, open StickerSense once, then choose **Open Anyway** in System Settings › Privacy & Security, or run:

```sh
xattr -dr com.apple.quarantine /Applications/StickerSense.app
```

You need macOS 27 or later on Apple silicon, with Apple Intelligence turned on. The Setup window then walks you through:

- allowing VoiceOver to be controlled with AppleScript (in VoiceOver Utility, General);
- Screen Recording and Accessibility permission;
- importing your WhatsApp stickers, if you'd like;
- a practice sticker.

The full Mac guide, troubleshooting included, is in [apple/README.md](apple/README.md).

### Windows

1. From [Releases](https://github.com/Pranav435/stickersense/releases/latest), download `StickerSense-Windows-x64.zip`. It's about 1.3 GB because the description model is inside, so StickerSense works as soon as it's unzipped. On a slow connection, download `StickerSense-Windows-x64-app-only.zip` (about 120 MB) instead, and Setup downloads the model (1.4 GB) for you.
2. Extract it: select the zip in File Explorer, open its context menu with the Applications key or Shift+F10, and choose Extract All. A folder such as `Apps\StickerSense` in your user folder works well.
3. Run `StickerSense.exe`. It isn't code-signed yet, so Windows SmartScreen may say "Windows protected your PC". Choose More info, then Run anyway. Windows only asks once.
4. The Setup window checks your screen reader and the model, and has a practice sticker. NVDA users get speech and braille through NVDA directly; the helper file for that is in the download.

You need Windows 10 (version 1809 or later) or Windows 11, 64-bit, with 8 GB of memory. No graphics card needed.

StickerSense lives in the notification area. Its windows are ordinary taskbar windows, so Alt+Tab reaches them, and starting StickerSense again brings up its main window. The full Windows guide, with NVDA, JAWS and Narrator tips and troubleshooting, is in [windows/README.md](windows/README.md).

### iPhone and iPad

Not released yet. The app builds from source (iOS 27, Apple Intelligence): a Back Tap or Action button shortcut describes the stickers on screen, newest first, and there's a share sheet option, Control Center controls and the picker. See [apple/README.md](apple/README.md#iphone-and-ipad).

## Shortcuts

They all work from any app, and you can change the letter keys in Settings.

| What it does | Mac | Windows |
|---|---|---|
| Describe what your screen reader is on; press twice for detail | Option Shift Command D | Control Alt Shift D |
| Open the sticker picker | Option Shift Command S, or type :: in a chat | Control Alt Shift S, or type :: in a chat |
| Make stickers of what you typed (in the picker) | Command G | Control G |
| Open the history | Option Shift Command H | Control Alt Shift H |
| Turn automatic descriptions on or off | Option Shift Command A | Control Alt Shift A |

Forgot them? On a Mac, choose "What Can StickerSense Do?" from the menu bar menu. On Windows, choose "Hear What StickerSense Can Do" in the main window. Either way, it reads them to you.

## How fast

| | Mac (Apple silicon) | Windows (8-core Intel processor, no graphics card) |
|---|---|---|
| A sticker it hasn't seen before | about 1.2 seconds | 3 to 4 seconds |
| An animated sticker or GIF | 3 to 5 seconds | 5 to 7 seconds |
| The detailed description | about 2.5 seconds | 8 to 14 seconds |
| A sticker it's seen before | instant | instant |

StickerSense also stays out of your screen reader's way. It only checks what you're on after you press a key (on Windows, when focus moves), keeps quiet while you switch apps, and describes your imported stickers only after you've left the computer alone for 90 seconds. Your screen reader should feel exactly as quick as before. If it doesn't, that's a bug: please [open an issue](#contributing).

## How it works

1. **Find the item.** On a Mac, StickerSense asks VoiceOver, through its AppleScript support, where its cursor is and what it's saying. On Windows, it follows UI Automation focus and the item's accessible name. On iPhone, a shortcut takes a screenshot.
2. **Look at it.** It captures only that rectangle of the screen, plus a few more frames if anything moves.
3. **Read and describe.** The system's own text recognition (Apple's Vision framework, or Windows OCR) reads the words. An on-device model describes the picture: Apple's foundation model on a Mac, and Qwen3-VL 2B (or Phi-3.5-vision, if you prefer) on Windows. To save time, it starts describing the first frame while it's still checking whether the sticker moves.
4. **Make stickers, when you ask.** The on-device language model plans the caption and four picture ideas. [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp) draws them with SDXS-512 or SD-Turbo (Metal on a Mac, Vulkan or the processor on Windows). Vision on a Mac, or ISNet on Windows, cuts out the subject. StickerSense writes the words and adds the border, then describes the result like any other sticker.
5. **Speak.** On a Mac the description goes to VoiceOver. On Windows it goes to NVDA or JAWS directly, to Narrator through UI Automation, or to the Windows voice if no screen reader is running. Either way, you hear it with your own voice settings, and it reaches your braille display.

## Privacy

- Pictures are described, and stickers made, on your computer. The only exception is an optional Mac setting that sends detailed descriptions to Apple's Private Cloud Compute, and it's off unless you turn it on.
- Photos are never saved. Only stickers and GIFs go into your local library, with names and times painted out.
- Your GIPHY key is kept in the Mac Keychain, or encrypted for your Windows account, not in a settings file.
- The activity log records what was said and how long it took. It never records images or your messages.

## Build it yourself

### Mac

You'll need Xcode 27 and XcodeGen (`brew install xcodegen`).

1. Find your signing certificate with `security find-identity -v -p codesigning`. Use one marked valid. The build script rejects revoked certificates, because macOS treats an app signed with one as malware and offers to throw it away.
2. Build and install:
   ```
   STICKERSENSE_SIGN_IDENTITY="<certificate SHA-1>" apple/scripts/build-mac.sh
   ```
   That builds StickerSense, puts it in `~/Applications` and opens it.

### Windows

You'll need the .NET 8 SDK (Visual Studio 2022 works too).

```
dotnet test windows\tests\StickerSense.Core.Tests
powershell -ExecutionPolicy Bypass -File windows\scripts\package-release.ps1 -Zip
```

The second command publishes StickerSense with the fast model, the sticker maker and NVDA's helper file into `windows\publish`, ready to unzip anywhere. [windows/README.md](windows/README.md) has the details, including `StickerSense.exe --self-test` for checking a PC.

### Releases

`scripts/release.sh <version>`, run on a Mac, does both platforms. It runs the tests, builds and signs the Mac app, publishes the GitHub release, and updates the Homebrew cask. The Windows zips are built on a Windows PC over SSH (`STICKERSENSE_WINDOWS_HOST`), or on GitHub Actions with `STICKERSENSE_WINDOWS_CI=1`. Add `--dry-run` to build everything without publishing.

## Repository layout

- `apple/StickerKit`: the shared Swift package (describing, caching, fingerprints, the library, picker search, GIPHY, sticker art), with unit tests and real on-device model tests.
- `apple/Mac`: the Mac app.
- `apple/iOS*`: the iPhone and iPad app, share extension and Control Center controls.
- `apple/project.yml`: the XcodeGen spec. The Xcode project is generated, so it isn't committed.
- `windows/src/StickerSense.Core`: the Windows core library (the same ideas as StickerKit, in C#), and `windows/tests` its tests.
- `windows/src/StickerSense.App`: the Windows app (.NET 8, WPF).
- `scripts/`: the release script and the donation helper.

To run the tests: `swift test` in `apple/StickerKit` (add `STICKERSENSE_MODEL_TESTS=1` to include the tests that run the real model), and `dotnet test windows/tests/StickerSense.Core.Tests`.

## Contributing

Bug reports from screen reader users help the most. Was something slow, wrong, too chatty or silent? Open an issue with the relevant lines from the activity log: `~/Library/Logs/StickerSense.log` on a Mac, or `%LOCALAPPDATA%\StickerSense\StickerSense.log` on Windows. On Windows, the output of `StickerSense.exe --self-test` helps too.

## Donate

StickerSense stays free. What costs money is getting rid of the warnings on first launch: Apple's notarization makes "Open Anyway" disappear and lets StickerSense into Homebrew's main cask list, and a Windows code-signing certificate does the same for "Windows protected your PC".

Notarization fund: **$0 of $99**.

- [Send a sticker, $3](https://paypal.me/theblindiephoenix/3USD): pays for a bug fix.
- [Send a GIF, $5](https://paypal.me/theblindiephoenix/5USD): pays for a bug fix, with motion.
- [Send a voice note, $20](https://paypal.me/theblindiephoenix/20USD): pays for a whole feature. Like most voice notes, it takes longer than anyone expected.
- [Cover Apple's fee for a year, $99](https://paypal.me/theblindiephoenix/99USD): notarization, so "Open Anyway" goes away for everyone.
- [Pick any amount](https://paypal.me/theblindiephoenix), in any currency.

If StickerSense ever kept you from replying "lol" to a crying sticker, that's worth about one sticker. Can't spare the money? Star the repo instead. It's GitHub's thumbs up, without the passive aggression.

### Thanks to

Want your name here? Say so in the note on your PayPal payment.

<!-- donors: scripts/donation.sh adds names below this line -->
Nobody has sent a sticker yet, so the first name here could be yours.

## License

MIT. See [LICENSE](LICENSE). On Windows, the release also includes Qwen3-VL 2B (Apache 2.0) and NVDA's controller client (LGPL 2.1); [windows/README.md](windows/README.md) lists them. Both apps include stable-diffusion.cpp (MIT) for making stickers. Its models are downloaded on first use: SDXS-512 (OpenRAIL++), or SD-Turbo if you choose it (Stability AI Community License; powered by Stability AI), and on Windows ISNet (Apache 2.0). The Mac app writes WhatsApp stickers with libwebp (BSD).
