# StickerSense

Someone sends you a sticker. Your screen reader, ever the diplomat, says "sticker". Thanks. Was it a cat crying with laughter, or a passive-aggressive thumbs up? Should you reply "haha" or apologise? Without knowing what's in it, you can only guess.

StickerSense describes stickers, GIFs and pictures out loud, in any app, for people who use a screen reader. It runs on your own device. It's quick enough that you'll hear what the sticker shows before the moment has passed.

> "Dog with tongue out and wide eyes, 'idk lol'."
>
> That's a real description, straight from StickerSense's own tests.

Free, no ads, no tracking. If it saves you from answering a crying sticker with "lol", [buy its developer a coffee](#donate).

## What it does

- **Describes what you're on.** Put the VoiceOver cursor on a sticker and press Option Shift Command D. You hear a short description. Press it again within 3 seconds and you get the director's cut: colours, art style, what moves, and what people mean when they send it.
- **Describes stickers automatically.** In WhatsApp, Messages, Telegram, Discord, Signal, Slack, Messenger and chat websites, when your cursor rests on a sticker or GIF, StickerSense speaks right after your screen reader's own announcement. No keypress needed.
- **Describes animations.** It grabs a few frames and tells you what happens: "Yellow cat, eyes open, then become hearts while mouth opens." Animated stickers no longer just "move" at you.
- **Reads the words on stickers exactly.** It uses on-device text recognition, and it only reports words that are really there. The language model is a fine describer but a creative reader; it once insisted a dumpling said "red lips, green eyes". So it no longer gets a vote on what the text says.
- **Ignores group chat clutter.** The sender's name and timestamp are painted out before describing, so "Ana" is never read as the sticker's caption.
- **Remembers everything it describes.** The second time a sticker turns up, from anyone, the description is instant.
- **Includes a sticker picker made for screen readers.** Type two colons (::) in a chat, or press Option Shift Command S, then search by what a sticker shows, says or means: "birthday", "lol", "thank you". It searches stickers you've heard, your WhatsApp stickers, and GIPHY 50 at a time. Press Return and it pastes into your chat, replacing the colons. It never presses Send for you; that's still your decision.
- **Keeps a history** of everything described, with the detailed version one keypress away.

## How fast

On a recent Apple silicon Mac:

- A sticker it hasn't seen before: about 1.2 seconds.
- The detailed description: about 2.5 seconds.
- A sticker it's seen before: instant.

It also stays out of VoiceOver's way. It only checks the cursor after you press a key, goes quiet while you switch apps, and describes your imported stickers only once you've stopped using the keyboard and trackpad for 90 seconds. Your screen reader should feel exactly as quick as before.

## Platforms

- **Mac** (macOS 27 or later, Apple silicon, Apple Intelligence on). The main event: fully built, and tested by a VoiceOver user.
- **iPhone and iPad** (iOS 27 or later, Apple Intelligence). iOS won't let apps see the screen or the VoiceOver cursor, so it works through a Back Tap or Action button shortcut that describes the stickers on screen, newest first. There's also a share sheet option, Control Center controls and the picker. It builds; it hasn't been tested on a device yet.
- **Windows** (10 and 11, with NVDA, JAWS or Narrator). Same idea and same shortcuts with Control Alt Shift, using an on-device vision model. It has been installed and run on one Windows 11 PC with NVDA: its default model, Qwen3-VL 2B, describes a still sticker in 3 to 4 seconds on the processor, and an animated one in 5 to 7. Trying the shortcut, automatic descriptions and "::" by keyboard in a real chat is still to do. If you try it, please report what happens.

## Install on a Mac

With [Homebrew](https://brew.sh), and `brew upgrade` keeps it current:

```sh
brew install --cask pranav435/tap/stickersense
```

Or by hand: download `StickerSense.zip` from [Releases](https://github.com/Pranav435/stickersense/releases/latest), unzip it, and move StickerSense to Applications.

Either way, the first launch takes one extra step because the app isn't notarized. Notarizing costs $99 a year, and StickerSense costs nothing ([Donate](#donate) has a plan for that). Open StickerSense once, then go to System Settings › Privacy & Security and choose **Open Anyway**, or run:

```sh
xattr -dr com.apple.quarantine /Applications/StickerSense.app
```

Requires macOS 27 or later on Apple silicon, with Apple Intelligence turned on.

When it opens, the Setup window asks you to:

- allow VoiceOver to be controlled with AppleScript (in VoiceOver Utility, General);
- give Screen Recording and Accessibility permission;
- optionally import your WhatsApp stickers;
- try it out on a practice sticker.

The full guide, troubleshooting included, is in [apple/README.md](apple/README.md). For Windows, see [windows/README.md](windows/README.md).

## Build it yourself

You'll need Xcode 27 and XcodeGen (`brew install xcodegen`).

1. Find your signing certificate with `security find-identity -v -p codesigning`. Use one marked valid; the build script rejects revoked ones, because macOS treats an app signed with a revoked certificate as malware and offers to throw it away.
2. Build and install:
   ```
   STICKERSENSE_SIGN_IDENTITY="<certificate SHA-1>" apple/scripts/build-mac.sh
   ```
   That builds StickerSense, puts it in `~/Applications` and opens it.

Releases come from `apple/scripts/release-mac.sh <version>`. It runs the tests, builds and zips the app, publishes the GitHub release and updates the Homebrew cask.

## Shortcuts

All of these work from any app. You can change the letter keys in Settings.

- **Option Shift Command D:** describe what the VoiceOver cursor is on. Press it twice for detail.
- **Option Shift Command S**, or type **::** in a chat: open the sticker picker.
- **Option Shift Command H:** open the history.
- **Option Shift Command A:** turn automatic descriptions on or off.

Forgot them? Choose "What Can StickerSense Do?" from the menu bar menu and it will read them to you.

## How it works

1. **Find the item.** StickerSense asks VoiceOver, through VoiceOver's AppleScript support, where its cursor is and what it's saying. On Windows it follows UI Automation focus. On iPhone, a shortcut takes a screenshot.
2. **Look at it.** It captures only that rectangle of the screen, plus a few more frames if anything moves.
3. **Read and describe.** Apple's Vision framework reads the text. Apple's on-device foundation model (Qwen3-VL or Phi-3.5-vision on Windows) describes the picture. For speed, it starts describing the first frame while still checking whether the sticker is animated.
4. **Speak.** The description goes to VoiceOver, so it's spoken in your voice settings and also appears on your braille display.

## Privacy

- Pictures are described on your device. The only exception is an optional setting that sends detailed descriptions to Apple's Private Cloud Compute, and it's off unless you turn it on.
- Photos are never saved. Only stickers and GIFs go into your local library, with names and times painted out.
- Your GIPHY key lives in the Keychain (or Windows DPAPI), not in a settings file.
- The activity log records what was said and how long it took. It never records images or your messages.

## Repository layout

- `apple/StickerKit`: the shared Swift package — describing, caching, fingerprints, the library, picker search, GIPHY — with unit tests and real on-device model tests.
- `apple/Mac`: the Mac app.
- `apple/iOS*`: the iPhone and iPad app, share extension and Control Center controls.
- `apple/project.yml`: the XcodeGen spec. The Xcode project is generated, so it isn't committed.
- `windows/`: the Windows app (C#, .NET 8, WPF) with its own core library and tests.

To run the tests, use `swift test` in `apple/StickerKit`; add `STICKERSENSE_MODEL_TESTS=1` to include the tests that run the real model. For Windows, use `dotnet test windows/tests/StickerSense.Core.Tests`.

## Contributing

Bug reports from screen reader users help the most. Was something slow, wrong, too chatty or silent? Open an issue and include the relevant lines from `~/Library/Logs/StickerSense.log`. If you have a Windows PC and NVDA, you could be the first person to use the Windows version day to day, which is still quite a title.

## Donate

StickerSense has no ads, no account, no subscription and no analytics, and it describes everything on your own device. This section is the only part of the project that wants anything from you.

Notarization fund: **$0 of $99**.

| Send | What it does |
|---|---|
| [A sticker, $3](https://paypal.me/theblindiephoenix/3USD) | Covers one coffee and one bug fix, in that order |
| [A GIF, $5](https://paypal.me/theblindiephoenix/5USD) | About the price of a café coffee, which lasts an afternoon. A cached sticker description lasts forever |
| [A whole sticker pack, $20](https://paypal.me/theblindiephoenix/20USD) | Fuels a whole feature, from the first idea to the 2 AM bug it ships with |
| [A year of notarization, $99](https://paypal.me/theblindiephoenix/99USD) | Goes toward Apple's developer fee, so StickerSense can be notarized: "Open Anyway" disappears for everyone, and it can join Homebrew's main cask list |
| [Your own amount](https://paypal.me/theblindiephoenix) | All currencies are converted to coffee at a fair rate |

If StickerSense has ever told you what a sticker meant before you replied, that's about one coffee's worth. Can't spare the money? A star on the repo costs nothing, and yes, someone checks.

### Fueled by

Add a note to your PayPal payment if you'd like your name here.

<!-- donors: scripts/donation.sh adds names below this line -->
Nobody has sent a sticker yet, so the first name here could be yours.

## License

MIT. See [LICENSE](LICENSE).
