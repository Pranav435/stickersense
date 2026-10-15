# StickerSense for Windows

StickerSense describes chat stickers, GIFs, emoji and memes out loud, for people who use a screen reader. It runs entirely on your computer: pictures never leave it.

It works with NVDA, JAWS and Narrator, and speaks with the Windows voice when no screen reader is running. Everything it says also goes to your braille display through your screen reader.

This is the Windows version of the Mac app in this repository and behaves the same way.

## What it does

- Press Control Alt Shift D on a sticker to hear a short description, such as "Shiba dog shrugging, idk".
- Press it again within 3 seconds for a detailed description. A third press repeats the detail.
- When focus rests on a sticker or GIF in a chat app, or on a sticker picture in a browser, StickerSense describes it automatically. Photos, text messages, and text that only talks about stickers are left alone.
- Animated stickers and GIFs are described with their motion, such as "Cartoon cat waves, then winks".
- Words written on a sticker are read exactly, using Windows text recognition. StickerSense only says a sticker has words when text recognition actually found them, so it doesn't invent any.
- In group chats, the sender's name and the time around a sticker are ignored, so the same sticker is recognised whoever sent it.
- A sticker you have heard before is described instantly from memory, and coming back to one speaks it again. An animated sticker or GIF is recognised whichever frame a chat shows, including the paused preview, so it always gets the same description.
- A GIF you sent from the picker ("Your GIF" in the chat) is recognised and described from the original file.
- The sticker picker finds stickers by what they show, say or mean, and pastes one into your chat. It never sends it: you press Enter yourself.
- In a chat's message box, type two colons (::) to open the sticker picker. The sticker you pick replaces the colons.
- Can't find the right sticker? Press Control G in the picker and StickerSense makes 4 of your own from what you typed, on this computer, and reads each one out as it's ready.

## Keyboard shortcuts

All shortcuts work everywhere. You can change the last key of each in Settings.

- Control Alt Shift D: describe what has focus. Press twice quickly for detail.
- Control Alt Shift S: open the sticker picker.
- Control Alt Shift H: open the sticker history.
- Control Alt Shift A: turn automatic descriptions on or off.

If another app already uses one of these, StickerSense tells you when it starts, and you can pick another key in Settings.

You can also type two colons (::) in a chat's message box to open the sticker picker; see below.

## Requirements

- Windows 10 version 1809 or later, or Windows 11. 64-bit Intel or AMD (x64), or ARM64.
- The fast model (Qwen3-VL 2B, the default) runs on the processor: 8 gigabytes of memory or more. It uses about 2 gigabytes while StickerSense runs, and 1.4 gigabytes of disk.
- Optionally, Phi-3.5-vision: for a DirectX 12 graphics card with at least 2 gigabytes of its own memory (2.6 gigabytes of disk), or for the processor (16 gigabytes of memory recommended, 3.2 gigabytes of disk). On a processor it's about two and a half times slower than the fast model.

## Installing

### From a release (model included)

1. From [Releases](https://github.com/Pranav435/stickersense/releases/latest), download `StickerSense-Windows-x64.zip` (about 1.5 GB, with the fast model, so it works offline as soon as it's unzipped). On a slow connection, download `StickerSense-Windows-x64-app-only.zip` instead; Setup then downloads the model (1.4 GB).
2. Extract it: select the zip in File Explorer, press the Applications key or Shift+F10, and choose Extract All. A folder you can write to works best, such as `%LOCALAPPDATA%\Programs\StickerSense` or `Apps\StickerSense` in your user folder.
3. Run `StickerSense.exe`. StickerSense isn't code-signed yet, so the first time Windows SmartScreen may say "Windows protected your PC". Choose More info, then Run anyway.
4. The Setup window opens the first time. It checks the model, your screen reader and text recognition, and has a practice sticker.

After that, starting StickerSense opens its main window (see below). Closing the window doesn't quit StickerSense: it keeps running in the notification area, so the shortcuts keep working. When Windows starts it at sign-in, it stays quietly in the notification area without opening a window.

To get back to StickerSense:
- Start it again from the Start menu or the desktop: the running copy shows its main window.
- Or press Windows+B, arrow to StickerSense in the notification area, and press Enter for the main window, or the Applications key for its menu.

Every StickerSense window (main window, picker, history, settings, setup) is an ordinary window: it's on the taskbar and you can switch to it with Alt+Tab.

## The main window

The StickerSense window has three parts, each with a heading:

- Status: whether the on-device model is ready, a checkbox to describe stickers automatically, and how many stickers are in your library (and how many are still waiting to be described).
- Open: buttons for the sticker picker, sticker history, settings, setup, and Hear What StickerSense Can Do, which speaks every shortcut.
- Shortcuts, from any app: a list of the shortcuts, spelled out.

### Without the model

If you have the app without the model, Setup offers to download one from Hugging Face. Choose:

- The fast model, Qwen3-VL 2B, for the processor: about 1.4 gigabytes. Recommended for most computers, including every laptop with built-in graphics.
- Phi-3.5-vision for the graphics card: about 2.6 gigabytes. Recommended when your graphics card has 2 gigabytes or more of its own memory.
- Phi-3.5-vision for the processor: about 3.2 gigabytes.

Setup marks the one it recommends for your computer. The model you download last is the one used; with more than one installed, choose in Settings.

StickerSense announces the download progress every 10 percent. You can keep using your computer, cancel, and continue later from where it stopped.

## The description model

StickerSense describes stickers with one of two on-device vision models, int4 ONNX builds run with ONNX Runtime GenAI:

- Qwen3-VL-2B-Instruct (the default, "the fast model"), from [onnx-community/Qwen3-VL-2B-Instruct-ONNX](https://huggingface.co/onnx-community/Qwen3-VL-2B-Instruct-ONNX), folder `onnxruntime/cpu_and_mobile/cpu-int4-rtn-block-32`. It runs on the processor.
- Phi-3.5-vision-instruct, from [microsoft/Phi-3.5-vision-instruct-onnx](https://huggingface.co/microsoft/Phi-3.5-vision-instruct-onnx): `gpu/gpu-int4-rtn-block-32` for graphics cards (DirectML), or `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4` for the processor.

StickerSense looks for a model in this order, the one chosen in Settings first:

1. `models\qwen3-vl-2b\` next to `StickerSense.exe` (the bundled release), then `%LOCALAPPDATA%\StickerSense\models\qwen3-vl-2b\` (downloaded from Setup).
2. `models\phi-3.5-vision\` next to `StickerSense.exe`, then `%LOCALAPPDATA%\StickerSense\models\phi-3.5-vision\`.

Phi-3.5-vision's graphics card build runs through DirectML. If the graphics card can't run it, StickerSense switches to the processor by itself. On built-in graphics that share the computer's memory, StickerSense runs it on the processor from the start, because that's faster there. In Settings you can choose "The graphics card" or "The processor only" instead.

The model runs in a separate background process (`StickerSense.exe --engine`), so a problem inside the model can't close StickerSense. If that process stops, StickerSense says the description failed and starts it again.

### How fast is it?

Measured on an Intel Core i7-11700 (8 cores, 16 gigabytes, built-in Intel UHD Graphics 750), middle of three runs:

| | The fast model (Qwen3-VL 2B) | Phi-3.5-vision, processor |
|---|---|---|
| Loading when StickerSense starts | about 4 seconds | about 6 seconds |
| A still sticker | 3 to 4 seconds | about 10 seconds (11 the first time) |
| An animated sticker or GIF | 5 to 7 seconds | about 22 seconds |
| The detailed description | 8 to 14 seconds | 28 to 40 seconds |
| A sticker heard before | instant | instant |

On that computer's built-in graphics, Phi-3.5-vision's graphics card build took about 100 seconds a sticker, which is why StickerSense uses the processor there. A graphics card with its own memory is much faster.

How it stays quick:

- Pictures go to the model small: Qwen3-VL spends one token on every 32 by 32 pixels, so a quick reading gets a 336-pixel picture (frames of an animation, 256 pixels each). The detailed description gets larger ones. Phi-3.5-vision gets each picture as one 336-pixel square plus an overview, instead of the 17 tiles ONNX Runtime's own image processor makes, about 6 times faster; an animation's frames go to it side by side in one picture, with one overview for all of them.
- The quick reading asks for just the lines that are spoken, and stops as soon as they're written. The detailed description stops after five sentences.
- The words on a sticker come from Windows text recognition, not the model.

Descriptions are in your Windows display language. Text written on a sticker is kept in its own language.

## Screen readers

### NVDA

StickerSense talks to NVDA directly through NVDA's controller client, `nvdaControllerClient.dll`, which comes with the StickerSense download. StickerSense speaks in your NVDA voice and shows the text on your braille display.

If you built StickerSense yourself, or the Setup window says the DLL is missing, add it:

1. Go to NVDA's download folder, for example [download.nvaccess.org/releases/stable](https://download.nvaccess.org/releases/stable/).
2. Download the file whose name ends in `_controllerClient.zip`.
3. Open the zip. On a 64-bit Intel or AMD computer, take `x64\nvdaControllerClient.dll`. On ARM64, take `arm64\nvdaControllerClient.dll`.
4. Put it in the same folder as `StickerSense.exe`, then restart StickerSense.

The Setup window tells you whether StickerSense found the DLL. Its Open the StickerSense Folder button opens the right folder. Older NVDA packages named the 64-bit file `nvdaControllerClient64.dll`; that name works too.

Without the DLL, StickerSense speaks with the Windows voice instead, and nothing goes to your braille display. That's because NVDA only reads notifications from the app you're using, and while you read a chat, that's the chat app, not StickerSense.

NVDA's browse mode moves a virtual cursor, not the real focus. Automatic descriptions and the shortcut follow the real focus. In chat apps such as WhatsApp, move through messages with the arrow keys in the message list, or switch to focus mode. In NVDA's settings, under Browse Mode, turning on "Automatically set system focus to focusable elements" also helps. If the shortcut says focus is on a large area, move focus to the message itself.

### JAWS

StickerSense speaks through JAWS's own programming interface, which JAWS installs. Nothing extra is needed. As with NVDA, the virtual cursor doesn't move focus, so arrow through messages in focus mode or with the JAWS cursor routed to the message.

### Narrator

StickerSense speaks through Narrator notifications. Nothing extra is needed. Narrator's scan mode moves focus to items that can take focus.

### No screen reader

StickerSense speaks with the Windows voice. In Settings, you can choose the Windows voice even when a screen reader is running.

## The sticker picker

Press Control Alt Shift S in a chat, or type two colons (::) in the chat's message box. StickerSense remembers which chat you were in. When you opened the picker with two colons, the sticker you pick replaces them; press Escape to close the picker and keep the colons as typed.

Typing two colons only opens the picker in chat apps and browsers (or every app, if automatic descriptions watch every app), only in a text box, and only when you type the two colons within a second with nothing in between. StickerSense notices them through Windows' raw keyboard input, which doesn't slow down your typing or your screen reader, and it keeps no record of what you type.

1. Type what the sticker shows, says or means, such as birthday, thank you or lol. Your own stickers appear at once. StickerSense announces how many results it found and reads the first one, for example "3 of yours and 50 from GIPHY. First: …". If GIPHY is slow, you hear your own results first and "Plus 50 from GIPHY" when they arrive.
2. Press Enter to paste the first result, or Down Arrow to go to the list.
3. In the list, press Enter to paste the sticker you're on.
4. GIPHY results come 50 at a time. The last row, "More GIPHY results", loads the next 50 when you press Enter on it; StickerSense also loads more on its own as you get near the end of the list.

StickerSense pastes into your chat and says "Pasted into WhatsApp. Press Enter to send." It never presses Enter for you.

More actions in the list:

- Control+D: describe in detail.
- Control+C: copy, without pasting.
- Delete: remove from your library.
- Shift+F10 or the Applications key: all actions in a menu.
- Escape: close the picker and go back to your chat.

### Making your own stickers

As soon as you type in the search box, a Generate sticker button appears after it. Press it, or Control G (or Alt G) from anywhere in the picker, and StickerSense makes 4 stickers of what you typed, on this computer:

1. It plans them with the description model: the words to print, when your search is something people say ("lol", "thank you", "happy birthday"), and four different ideas for the picture. Words in quotes are always printed exactly as typed: `corgi "ty <3"`.
2. It draws each picture with a small image model, lifts the subject off its background, writes the words in bold lettering, and puts a white die-cut border round it, like a WhatsApp sticker.
3. Each sticker is described like any other and read out as it's ready: "Sticker 1 of 4: Cartoon corgi bowing, “thank you”. It's at the top of the list." The new stickers stay at the top of the list until you close the picker.

Press Enter on one to paste it, like any other result. Only the stickers you paste or copy are kept, in your library under Made by you; searching for the same words finds them again. Typing something else, or closing the picker, stops the stickers still being made.

Without a graphics card it takes a while: on GitHub's 4-core Windows test machines, planning takes about 15 seconds and each sticker about 15 seconds to draw and 12 to describe, so the first one is ready after about 45 seconds and all 4 after about 2 minutes. A faster processor is quicker, and a graphics card with memory of its own is used for drawing when there is one. Your screen reader keeps working meanwhile.

The first time, StickerSense asks before downloading the sticker maker's models: 1.1 gigabytes for Fast (SDXS-512 and the cut-out model), or 2.2 gigabytes for Better (SD-Turbo), from Hugging Face and GitHub. It says how the download is going every quarter, and you can keep using StickerSense meanwhile. You can also download it ahead of time from Setup or Settings. Choose the model in Settings: Fast is quicker; Better follows your search more closely and varies more.

Stickers come from four places:

- Received: every sticker StickerSense has described for you.
- WhatsApp: stickers WhatsApp Desktop keeps on this computer. Use Import WhatsApp Stickers in Setup or Settings. StickerSense only reads WhatsApp's files; it never changes them. Imported stickers are described in the background, but only while your computer is idle: nobody has typed or used the mouse for 90 seconds, and battery saver is off. Describing stops the moment you press a key, so it never slows your screen reader down. Until then, an imported sticker is described when you arrow onto it in the picker.
- Made by you: stickers you made with Control G and then pasted or copied.
- GIPHY: search results, if you add a free GIPHY API key in Settings. Get one at [developers.giphy.com](https://developers.giphy.com/dashboard/). The key is stored encrypted for your Windows account. GIPHY's own titles rarely say what's in the picture, so StickerSense describes the results on this computer, starting with the one you're on, and remembers those descriptions (up to 3000) so a GIF is only described once.

## Staying out of your screen reader's way

StickerSense is built not to slow your screen reader down:

- Automatic descriptions only start when focus rests on a sticker for a moment (about a fifth of a second). Arrowing past stickers doesn't start anything.
- For about a second and a half after you switch apps, StickerSense doesn't look at anything, while your screen reader catches up.
- Deciding whether something is a sticker uses only what Windows sends with each focus change. StickerSense only asks the chat app for more when an item has no useful name.
- Only one description runs at a time. Moving to another item and pressing the shortcut drops the old request.
- Speech is handed to your screen reader and never checked afterwards.

## Sticker history

Press Control Alt Shift H. Recent descriptions are listed newest first.

- Enter: hear the detailed description.
- Space: hear the short description again.
- Control+C: copy the description.
- Delete: remove it.
- Clear History: remove everything (you're asked to confirm).

## Settings

Open Settings from the StickerSense menu. You can:

- Turn automatic descriptions on or off, and choose chat apps and browsers only, or every app.
- Add what the sticker means to short descriptions.
- Type two colons (::) in a chat to open the sticker picker. On by default.
- Choose the description model, when more than one is installed: automatically the fast model, or Phi-3.5-vision.
- Choose where Phi-3.5-vision runs: automatically (the graphics card if it has its own memory, otherwise the processor), the graphics card, or the processor only.
- Turn the quiet sounds on or off, and set their volume.
- Choose your screen reader or the Windows voice for speech.
- Change the shortcut keys.
- Import WhatsApp stickers and add a GIPHY key.
- Choose the sticker model for making your own stickers, Fast or Better, and download it ahead of time.
- Open StickerSense when you sign in to Windows.

## Sounds

StickerSense plays short, quiet tones so you know what's happening without extra speech:

- A soft rising blip: working on it. Skipped when the answer comes from memory.
- A gentle two-note chime: the description is about to be spoken.
- A low double tone: something went wrong; the reason is spoken.
- A short tick: added, copied, or switched.

## Privacy

Describing and making stickers happen on this computer. StickerSense only uses the internet to download its models, to search GIPHY when you've added a key, and to download GIPHY results you choose. What you type to make a sticker never leaves the computer.

Your data is in `%LOCALAPPDATA%\StickerSense`:

- `library.json` and the `images` folder: stickers StickerSense has described, and ones you made and sent. Photos are remembered as descriptions only; their pictures are not kept.
- `models`: the description model and the sticker maker's models, when downloaded here.
- `settings.json`: your settings.
- `giphy-key.bin`: your GIPHY key, encrypted for your Windows account.
- `StickerSense.log`: an activity log for troubleshooting. It never contains pictures.

## Building from source

The code is in three projects:

- `src\StickerSense.Core`: everything that isn't tied to Windows. Fingerprints, the library, search, prompts, spoken text, GIPHY, image handling. It builds and tests on any system.
- `src\StickerSense.App`: the Windows app. WPF windows, the tray icon, UI Automation, screen capture, OCR, screen reader output, and the ONNX Runtime GenAI engine.
- `tests\StickerSense.Core.Tests`: unit tests for Core.

### With Visual Studio 2022

Install Visual Studio 2022 17.8 or later with the ".NET desktop development" workload. Open `StickerSense.sln`, set StickerSense.App as the startup project, and build.

### With the dotnet command line

You need the .NET 8 SDK.

```
dotnet build windows\StickerSense.sln
dotnet test windows\tests\StickerSense.Core.Tests
dotnet run --project windows\src\StickerSense.App
```

When running from source, put the model in `%LOCALAPPDATA%\StickerSense\models\phi-3.5-vision\` or download it from Setup. To use NVDA's controller client while developing, put `nvdaControllerClient.dll` in `windows\src\StickerSense.App\`; the build copies it to the output folder. It's ignored by git.

On macOS or Linux, Core and its tests build normally. The app also compiles, but can't run:

```
dotnet test windows/tests/StickerSense.Core.Tests
dotnet build windows/src/StickerSense.App -p:EnableWindowsTargeting=true
```

### Making the bundled release

On Windows, run:

```
powershell -ExecutionPolicy Bypass -File windows\scripts\package-release.ps1
```

This publishes a self-contained build to `windows\publish\StickerSense-win-x64` and downloads the fast model into its `models\qwen3-vl-2b` folder. Options:

- `-Runtime win-arm64`: build for ARM64.
- `-Variant DirectML`: bundle Phi-3.5-vision for graphics cards instead, or `-Variant Cpu` for Phi-3.5-vision on the processor.
- `-SkipModel`: leave the model out; users download it from Setup.
- `-SkipNvdaController`: leave out NVDA's controller client. By default the script downloads it from NV Access (a pinned version, checked against its SHA-256) and puts the DLL and its licence next to `StickerSense.exe`.
- `-NvdaControllerDll path`: include this `nvdaControllerClient.dll` instead of downloading one.
- `-Zip`: also make a zip file.

Running the script again resumes an interrupted model download.

### Testing on a Windows PC

`StickerSense.exe` has a few command-line modes for testing and bug reports. They print plain text and JSON, and don't touch a running StickerSense:

- `--self-test [--out report.json] [--model-dir folder] [--device auto|cpu|gpu] [--no-models] [--repeat N] [--fixture file] [--no-detail] [--threads N] [--warmup]`: checks what can be checked without a person at the keyboard (graphics, screen reader detection and speech route, icon, image decoding, GIPHY parsing, the library, text recognition) and times the model's quick and detailed descriptions of two built-in test stickers plus any `--fixture` files, `--repeat` times each (reporting the middle time and every text). Exit code 0 when everything passed.
- `--download-model qwen|directml|cpu [--to folder]`: downloads a model the way Setup does, printing progress, size and speed.
- `--test-window`: a small stand-in chat with a sticker (an image named "Sticker from Test User, 10:00") and a message box.
- `--watch-app name` (with a normal start): also treats that app as a chat app, for example `--watch-app StickerSense` for the test window. Not saved.
- `--quit`: closes the running StickerSense, as Quit in its menu does.

`windows\scripts\e2e-test.ps1` drives an installed StickerSense end to end with UI Automation and key presses: Setup, the main window, the notification area icon, the describe shortcut and its detail, automatic descriptions, and "::" with the picker. Run it on the signed-in, unlocked desktop (not over SSH); it closes everything it opens.

## Troubleshooting

- "There's nothing visible there to describe": the area under focus is one flat colour, for example an empty part of a window or a protected video. Blank areas are never described or remembered.
- "Focus is on a large area": focus is on a whole chat or window. Move focus onto the message or sticker. When nothing has focus, StickerSense describes a square around the mouse pointer instead.
- Nothing is spoken automatically: check that automatic descriptions are on (Control Alt Shift A) and that focus really moves to the sticker; see the browse mode notes above. Right after switching apps, StickerSense waits a moment before describing.
- Imported WhatsApp stickers aren't described yet: that only happens while the computer is idle for 90 seconds or more, with battery saver off.
- "The description model isn't installed yet": open Setup and download it.
- "The description model couldn't start": choose "The processor only" in Settings, or download the fast model in Setup.
- The first description after starting takes a while: the model loads in the background when StickerSense starts (about 4 seconds for the fast model). After that, a description takes 3 to 7 seconds with the fast model on a recent processor. Stickers heard before are instant. See "How fast is it?" above.
- Words on stickers aren't read exactly: install a text recognition (OCR) language. In Windows Settings, go to Time and language, Language and region, open your language's options, and add Optical character recognition.
- To see what happened, choose Show Activity Log in Settings.

## Third-party components

- ONNX Runtime and ONNX Runtime GenAI with DirectML, by Microsoft: MIT license.
- Qwen3-VL-2B-Instruct, by the Qwen team (Alibaba Cloud): Apache 2.0 license. ONNX build by onnx-community.
- Phi-3.5-vision-instruct, by Microsoft: MIT license.
- SixLabors.ImageSharp: Six Labors Split License (Apache 2.0 for open source projects and small organisations). StickerSense stays on version 3.1 because version 4 needs a license key to build. Known 3.1 security advisories are in parts it doesn't use: TIFF images and histogram equalisation. ICC colour profiles are never read.
- Interop.UIAutomationClient: MIT license.
- stable-diffusion.cpp, by leejet, and ggml: MIT license. The release includes its Vulkan build in the `sd` folder, with its licences (`stable-diffusion.cpp.txt`, `ggml.txt`).
- SDXS-512-DreamShaper, by IDKiro and others: OpenRAIL++ license. Downloaded on first use from [akleine/sdxs-512](https://huggingface.co/akleine/sdxs-512).
- SD-Turbo, by Stability AI: Stability AI Community License. Powered by Stability AI. Downloaded only if you choose the Better sticker model, as a GGUF build by Green-Sky, with madebyollin's TAESD decoder (MIT).
- ISNet (DIS) for anime and cartoons, by Xuebin Qin and SkyTNT: Apache 2.0 license. Downloaded on first use from rembg's model release.
- NVDA controller client, by NV Access: LGPL 2.1. The release includes `nvdaControllerClient.dll` unchanged, with its licence in `nvdaControllerClient-license.txt`. Its source code is at [github.com/nvaccess/nvda](https://github.com/nvaccess/nvda).
