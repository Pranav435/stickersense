# StickerSense

Someone sends you a sticker. Your screen reader, ever the diplomat, says "sticker". Thanks. Was it a cat crying with laughter, or a passive-aggressive thumbs up? Should you reply "haha" or apologise? Without knowing what's in it, you can only guess.

StickerSense describes stickers, GIFs and pictures out loud, in any app, for people who use a screen reader: VoiceOver on a Mac, and NVDA, JAWS or Narrator on Windows. It runs on your own computer. It's quick enough that you'll hear what the sticker shows before the moment has passed.

> "Dog with tongue out and wide eyes, 'idk lol'."
>
> That's a real description, straight from StickerSense's own tests.

Free, no ads, no tracking. If it saves you from answering a crying sticker with "lol", [buy its developer a coffee](#donate).

## What it does

- **Describes what you're on.** Put your screen reader on a sticker and press the describe shortcut: Option Shift Command D on a Mac, Control Alt Shift D on Windows. You hear a short description. Press it again within 3 seconds and you get the director's cut: colours, art style, what moves, and what people mean when they send it.
- **Describes stickers automatically.** In WhatsApp, Messages, Telegram, Discord, Signal, Slack, Messenger and chat websites, when you rest on a sticker or GIF, StickerSense speaks right after your screen reader's own announcement. Come back to it later and it speaks again, instantly. No keypress needed.
- **Describes animations.** It grabs a few frames and tells you what happens: "Yellow cat, eyes open, then become hearts while mouth opens." Animated stickers no longer just "move" at you.
- **Reads the words on stickers exactly.** It uses on-device text recognition, and it only reports words that are really there. The language model is a fine describer but a creative reader; it once insisted a dumpling said "red lips, green eyes". So it no longer gets a vote on what the text says.
- **Ignores group chat clutter.** The sender's name and timestamp are painted out before describing, so "Ana" is never read as the sticker's caption.
- **Remembers everything it describes.** The second time a sticker turns up, from anyone, the description is instant.
- **Includes a sticker picker made for screen readers.** Type two colons (::) in a chat, or press the picker shortcut, then search by what a sticker shows, says or means: "birthday", "lol", "thank you". It searches stickers you've heard, your WhatsApp stickers, and GIPHY 50 at a time. Choose one and it's pasted into your chat, replacing the colons. It never presses Send for you; that's still your decision.
- **Keeps a history** of everything described, with the detailed version one keypress away.

## Install

### On a Mac

With [Homebrew](https://brew.sh), and `brew upgrade` keeps it current:

```sh
brew install --cask pranav435/tap/stickersense
```

Or by hand: download `StickerSense-Mac.zip` from [Releases](https://github.com/Pranav435/stickersense/releases/latest), unzip it, and move StickerSense to Applications.

Either way, the first launch takes one extra step because the app isn't notarized. Notarizing costs $99 a year, and StickerSense costs nothing ([Donate](#donate) has a plan for that). Open StickerSense once, then go to System Settings › Privacy & Security and choose **Open Anyway**, or run:

```sh
xattr -dr com.apple.quarantine /Applications/StickerSense.app
```

Requires macOS 27 or later on Apple silicon, with Apple Intelligence turned on. When it opens, the Setup window asks you to:

- allow VoiceOver to be controlled with AppleScript (in VoiceOver Utility, General);
- give Screen Recording and Accessibility permission;
- optionally import your WhatsApp stickers;
- try it out on a practice sticker.

The full Mac guide, troubleshooting included, is in [apple/README.md](apple/README.md).

### On Windows

1. From [Releases](https://github.com/Pranav435/stickersense/releases/latest), download `StickerSense-Windows-x64.zip`. It's about 1.5 GB because it includes the on-device model, so StickerSense works the moment it's unzipped. On a slow connection, download `StickerSense-Windows-x64-app-only.zip` (about 100 MB) instead, and Setup downloads the model (1.4 GB) for you.
2. Extract it: select the zip in File Explorer, open its context menu with the Applications key or Shift+F10, and choose Extract All. A folder such as `Apps\StickerSense` in your user folder works well.
3. Run `StickerSense.exe`. It isn't code-signed yet, so Windows SmartScreen may say "Windows protected your PC". Choose More info, then Run anyway. You only do this once.
4. The Setup window checks your screen reader and the model, and has a practice sticker. NVDA users get speech and braille through NVDA directly; the helper file that needs comes in the download.

Requires Windows 10 (version 1809 or later) or Windows 11, 64-bit, with 8 GB of memory. No graphics card needed.

Once it's running, StickerSense lives in the notification area. Every one of its windows is an ordinary window on the taskbar, so Alt+Tab reaches it, and starting StickerSense again brings up its main window. The full Windows guide, with NVDA, JAWS and Narrator tips and troubleshooting, is in [windows/README.md](windows/README.md).

### On iPhone and iPad

Not released yet. The app builds from source (iOS 27, Apple Intelligence): a Back Tap or Action button shortcut describes the stickers on screen, newest first, plus a share sheet option, Control Center controls and the picker. See [apple/README.md](apple/README.md#iphone-and-ipad).

## Shortcuts

All of these work from any app. You can change the letter keys in Settings.

| What it does | Mac | Windows |
|---|---|---|
| Describe what your screen reader is on; press twice for detail | Option Shift Command D | Control Alt Shift D |
| Open the sticker picker | Option Shift Command S, or type :: in a chat | Control Alt Shift S, or type :: in a chat |
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

It also stays out of your screen reader's way. It only checks what you're on after you press a key (on Windows, when focus moves), goes quiet while you switch apps, and describes your imported stickers only once you've left the computer alone for 90 seconds. Your screen reader should feel exactly as quick as before.

## How it works

1. **Find the item.** On a Mac, StickerSense asks VoiceOver, through VoiceOver's AppleScript support, where its cursor is and what it's saying. On Windows, it follows UI Automation focus and the item's accessible name. On iPhone, a shortcut takes a screenshot.
2. **Look at it.** It captures only that rectangle of the screen, plus a few more frames if anything moves.
3. **Read and describe.** Text recognition built into the system (Apple's Vision framework, or Windows OCR) reads the words. An on-device model describes the picture: Apple's foundation model on a Mac, and Qwen3-VL 2B (or Phi-3.5-vision, if you prefer) on Windows. For speed, it starts describing the first frame while still checking whether the sticker is animated.
4. **Speak.** On a Mac the description goes to VoiceOver. On Windows it goes to NVDA or JAWS directly, to Narrator through UI Automation, or to the Windows voice if no screen reader is running. Either way, you hear it in your own voice settings and it reaches your braille display.

## Privacy

- Pictures are described on your computer. The only exception is an optional Mac setting that sends detailed descriptions to Apple's Private Cloud Compute, and it's off unless you turn it on.
- Photos are never saved. Only stickers and GIFs go into your local library, with names and times painted out.
- Your GIPHY key lives in the Mac Keychain, or encrypted for your Windows account, not in a settings file.
- The activity log records what was said and how long it took. It never records images or your messages.

## Build it yourself

### Mac

You'll need Xcode 27 and XcodeGen (`brew install xcodegen`).

1. Find your signing certificate with `security find-identity -v -p codesigning`. Use one marked valid; the build script rejects revoked ones, because macOS treats an app signed with a revoked certificate as malware and offers to throw it away.
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

The second command publishes StickerSense with the fast model and NVDA's helper file into `windows\publish`, ready to unzip anywhere. [windows/README.md](windows/README.md) has the details, including `StickerSense.exe --self-test` for checking a PC.

### Releases

`scripts/release.sh <version>`, run on a Mac, does both platforms: it runs the tests, builds and signs the Mac app, builds the Windows zips on a Windows PC over SSH (`STICKERSENSE_WINDOWS_HOST`), publishes the GitHub release and updates the Homebrew cask. Add `--dry-run` to build everything without publishing.

## Repository layout

- `apple/StickerKit`: the shared Swift package (describing, caching, fingerprints, the library, picker search, GIPHY), with unit tests and real on-device model tests.
- `apple/Mac`: the Mac app.
- `apple/iOS*`: the iPhone and iPad app, share extension and Control Center controls.
- `apple/project.yml`: the XcodeGen spec. The Xcode project is generated, so it isn't committed.
- `windows/src/StickerSense.Core`: the Windows core library (the same ideas as StickerKit, in C#), and `windows/tests` its tests.
- `windows/src/StickerSense.App`: the Windows app (.NET 8, WPF).
- `scripts/`: the release script and the donation helper.

To run the tests: `swift test` in `apple/StickerKit` (add `STICKERSENSE_MODEL_TESTS=1` to include the tests that run the real model), and `dotnet test windows/tests/StickerSense.Core.Tests`.

## Contributing

Bug reports from screen reader users help the most. Was something slow, wrong, too chatty or silent? Open an issue and include the relevant lines from the activity log: `~/Library/Logs/StickerSense.log` on a Mac, or `%LOCALAPPDATA%\StickerSense\StickerSense.log` on Windows. On Windows, the output of `StickerSense.exe --self-test` helps too.

## Donate

StickerSense has no ads, no account, no subscription and no analytics, and it describes everything on your own computer. This section is the only part of the project that wants anything from you.

Notarization fund: **$0 of $99**.

| Send | What it does |
|---|---|
| [A sticker, $3](https://paypal.me/theblindiephoenix/3USD) | Covers one coffee and one bug fix, in that order |
| [A GIF, $5](https://paypal.me/theblindiephoenix/5USD) | About the price of a café coffee, which lasts an afternoon. A cached sticker description lasts forever |
| [A whole sticker pack, $20](https://paypal.me/theblindiephoenix/20USD) | Fuels a whole feature, from the first idea to the 2 AM bug it ships with |
| [A year of notarization, $99](https://paypal.me/theblindiephoenix/99USD) | Goes toward Apple's developer fee, so StickerSense can be notarized: "Open Anyway" disappears for everyone, and it can join Homebrew's main cask list |
| [Your own amount](https://paypal.me/theblindiephoenix) | All currencies are converted to coffee at a fair rate |

After notarization, the next goal is a Windows code-signing certificate, so "Windows protected your PC" goes the same way as "Open Anyway".

If StickerSense has ever told you what a sticker meant before you replied, that's about one coffee's worth. Can't spare the money? A star on the repo costs nothing, and yes, someone checks.

### Fueled by

Add a note to your PayPal payment if you'd like your name here.

<!-- donors: scripts/donation.sh adds names below this line -->
Nobody has sent a sticker yet, so the first name here could be yours.

## License

MIT. See [LICENSE](LICENSE). On Windows, the release also includes Qwen3-VL 2B (Apache 2.0) and NVDA's controller client (LGPL 2.1); [windows/README.md](windows/README.md) lists them.
