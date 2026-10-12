# StickerSense for Windows

StickerSense describes chat stickers, GIFs, emoji and memes out loud, for people who use a screen reader. It runs entirely on your computer: pictures never leave it.

It works with NVDA, JAWS and Narrator, and speaks with the Windows voice when no screen reader is running. Everything it says also goes to your braille display through your screen reader.

This is the Windows version of the Mac app in this repository and behaves the same way.

## What it does

- Press Control Alt Shift D on a sticker to hear a short description, such as "Shiba dog shrugging, idk".
- Press it again within 3 seconds for a detailed description. A third press repeats the detail.
- When focus rests on a sticker or GIF in a chat app or browser, StickerSense describes it automatically. Photos and text messages are left alone.
- Animated stickers and GIFs are described with their motion, such as "Cartoon cat waves, then winks".
- Words written on a sticker are read exactly, using Windows text recognition. StickerSense only says a sticker has words when text recognition actually found them, so it doesn't invent any.
- In group chats, the sender's name and the time around a sticker are ignored, so the same sticker is recognised whoever sent it.
- A sticker you have heard before is described instantly from memory.
- The sticker picker finds stickers by what they show, say or mean, and pastes one into your chat. It never sends it: you press Enter yourself.
- In a chat's message box, type two colons (::) to open the sticker picker. The sticker you pick replaces the colons.

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
- For the graphics card model: a DirectX 12 graphics card. Most computers from the last several years have one, including built-in Intel and AMD graphics.
- For the processor-only model: 16 gigabytes of memory is recommended.
- About 4 gigabytes of free disk space for the model.

## Installing

### From a release folder (model included)

1. Unzip the release, for example to `C:\Program Files\StickerSense` or `%LOCALAPPDATA%\Programs\StickerSense`.
2. Run `StickerSense.exe`.
3. The Setup window opens the first time. It checks the model, your screen reader and text recognition, and has a practice sticker.

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

If you have the app without the model, Setup offers to download it from Hugging Face. Choose:

- Download for the graphics card: about 2.6 gigabytes. Recommended when you have a graphics card; it's much faster.
- Download for the processor only: about 3.2 gigabytes. Works on any computer, but slower.

StickerSense announces the download progress every 10 percent. You can keep using your computer, cancel, and continue later from where it stopped.

## The description model

StickerSense uses Microsoft's Phi-3.5-vision-instruct model, int4 ONNX build, from [microsoft/Phi-3.5-vision-instruct-onnx](https://huggingface.co/microsoft/Phi-3.5-vision-instruct-onnx), run with ONNX Runtime GenAI and DirectML.

StickerSense looks for the model in this order:

1. `models\phi-3.5-vision\` next to `StickerSense.exe` (the bundled release).
2. `%LOCALAPPDATA%\StickerSense\models\phi-3.5-vision\` (downloaded from Setup).

The folder must contain the files of one of these Hugging Face folders:

- Graphics card: `gpu/gpu-int4-rtn-block-32`
- Processor only: `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4`

The graphics card model runs through DirectML. If the graphics card can't run it, StickerSense switches to the processor by itself. You can also choose "The processor only" in Settings.

Descriptions are in your Windows display language. Text written on a sticker is kept in its own language.

## Screen readers

### NVDA

StickerSense talks to NVDA directly through NVDA's controller client, `nvdaControllerClient.dll`. This gives the best results: StickerSense speaks in your NVDA voice and shows the text on your braille display. The DLL is not included with StickerSense, so add it yourself:

1. Go to NVDA's download folder for your NVDA version, for example [download.nvaccess.org/releases/stable](https://download.nvaccess.org/releases/stable/).
2. Download the file whose name ends in `_controllerClient.zip`.
3. Open the zip. On a 64-bit Intel or AMD computer, take `x64\nvdaControllerClient.dll`. On ARM64, take `arm64\nvdaControllerClient.dll`.
4. Put it in the same folder as `StickerSense.exe`, then restart StickerSense.

The Setup window tells you whether StickerSense found the DLL. Its Open the StickerSense Folder button opens the right folder. Older NVDA packages named the 64-bit file `nvdaControllerClient64.dll`; that name works too. The controller client is licensed under the LGPL by NV Access.

Without the DLL, StickerSense sends its speech as Windows notifications. NVDA reads those in most cases, but not all.

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

Stickers come from three places:

- Received: every sticker StickerSense has described for you.
- WhatsApp: stickers WhatsApp Desktop keeps on this computer. Use Import WhatsApp Stickers in Setup or Settings. StickerSense only reads WhatsApp's files; it never changes them. Imported stickers are described in the background, but only while your computer is idle: nobody has typed or used the mouse for 90 seconds, and battery saver is off. Describing stops the moment you press a key, so it never slows your screen reader down. Until then, an imported sticker is described when you arrow onto it in the picker.
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
- Choose the graphics card or the processor for the model.
- Turn the quiet sounds on or off, and set their volume.
- Choose your screen reader or the Windows voice for speech.
- Change the shortcut keys.
- Import WhatsApp stickers and add a GIPHY key.
- Open StickerSense when you sign in to Windows.

## Sounds

StickerSense plays short, quiet tones so you know what's happening without extra speech:

- A soft rising blip: working on it. Skipped when the answer comes from memory.
- A gentle two-note chime: the description is about to be spoken.
- A low double tone: something went wrong; the reason is spoken.
- A short tick: added, copied, or switched.

## Privacy

Describing happens on this computer. StickerSense only uses the internet to download the model, to search GIPHY when you've added a key, and to download GIPHY results you choose.

Your data is in `%LOCALAPPDATA%\StickerSense`:

- `library.json` and the `images` folder: stickers StickerSense has described. Photos are remembered as descriptions only; their pictures are not kept.
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

This publishes a self-contained build to `windows\publish\StickerSense-win-x64` and downloads the graphics card model into its `models\phi-3.5-vision` folder. Options:

- `-Runtime win-arm64`: build for ARM64.
- `-Variant Cpu`: bundle the processor-only model instead.
- `-SkipModel`: leave the model out; users download it from Setup.
- `-NvdaControllerDll path`: copy the NVDA controller client into the release.
- `-Zip`: also make a zip file.

Running the script again resumes an interrupted model download.

## Troubleshooting

- "Focus is on a large area": focus is on a whole chat or window. Move focus onto the message or sticker. When nothing has focus, StickerSense describes a square around the mouse pointer instead.
- Nothing is spoken automatically: check that automatic descriptions are on (Control Alt Shift A) and that focus really moves to the sticker; see the browse mode notes above. Right after switching apps, StickerSense waits a moment before describing.
- Imported WhatsApp stickers aren't described yet: that only happens while the computer is idle for 90 seconds or more, with battery saver off.
- "The description model isn't installed yet": open Setup and download it.
- "The description model couldn't start": choose "The processor only" in Settings, or download the processor model in Setup.
- The first description after starting takes a while: the model loads in the background when StickerSense starts. After that, a description takes a few seconds on a graphics card, longer on the processor. Stickers heard before are instant.
- Words on stickers aren't read exactly: install a text recognition (OCR) language. In Windows Settings, go to Time and language, Language and region, open your language's options, and add Optical character recognition.
- To see what happened, choose Show Activity Log in Settings.

## Third-party components

- ONNX Runtime and ONNX Runtime GenAI with DirectML, by Microsoft: MIT license.
- Phi-3.5-vision-instruct, by Microsoft: MIT license.
- SixLabors.ImageSharp: Six Labors Split License (Apache 2.0 for open source projects and small organisations). StickerSense stays on version 3.1 because version 4 needs a license key to build. Known 3.1 security advisories are in parts it doesn't use: TIFF images and histogram equalisation. ICC colour profiles are never read.
- Interop.UIAutomationClient: MIT license.
- NVDA controller client, by NV Access: LGPL. Not included; you add it yourself.
