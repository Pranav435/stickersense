import AppKit
import StickerKit
import SwiftUI

@main
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    static func main() {
        if CommandLine.arguments.contains("--make-sticker") {
            Task { @MainActor in exit(await MakeStickerCommand.run(CommandLine.arguments)) }
            dispatchMain()
        }
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
    }

    private let settings = SettingsStore()
    private let voiceOver = VoiceOverBridge()
    private let log = DebugLog()
    private lazy var announcer = Announcer(voiceOver: voiceOver)
    private let earcons = Earcons()
    private lazy var model = AppModel(settings: settings, voiceOver: voiceOver, announcer: announcer, earcons: earcons, log: log)
    private lazy var capturer = ScreenCapturer(voiceOver: voiceOver)
    private lazy var controller = DescribeController(
        pipeline: model.pipeline, voiceOver: voiceOver, capturer: capturer, announcer: announcer,
        earcons: earcons, settings: settings, log: log
    )
    private let hotkeys = HotkeyCenter()
    private let colonTrigger = ColonTrigger()
    private let windows = WindowManager()
    private var statusItem: NSStatusItem?
    private var autoMenuItem: NSMenuItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        announcer.log = log
        installMainMenu()
        installStatusItem()
        model.applySettings()
        colonTrigger.onTrigger = { [weak self] app in
            self?.log.add("Typed :: in \(app.localizedName ?? "chat"), opening picker")
            self?.openPickerFromTyping(in: app)
        }
        applyColonTrigger()
        settings.changed = { [weak self] hotkeysChanged in
            guard let self else { return }
            self.applyColonTrigger()
            self.model.applySettings()
            if hotkeysChanged { self.registerHotkeys() }
            self.controller.setAutoDescribe(running: self.settings.autoDescribe)
        }
        registerHotkeys()
        controller.setAutoDescribe(running: settings.autoDescribe)
        // Vision's first text recognition takes ~20 s per process; get it out of the way now.
        TextReader.warmUp()
        Task { await model.pipeline.describer.prewarm() }
        model.startIndexing()
        log.add("Launched. Engine: \(StickerDescriber.status.explanation) VoiceOver running: \(voiceOver.isRunning)")

        Task {
            await capturer.prime()
            // Only show Setup when something the user needs is actually missing.
            if !settings.hasCompletedSetup {
                let setupModel = SetupModel(app: model)
                await setupModel.refresh()
                if setupModel.allRequiredOK {
                    settings.hasCompletedSetup = true
                } else {
                    showSetup()
                    return
                }
            }
            // Opened by the user (not at login): show the main window, like any app.
            if !launchedAtLogin, !windows.hasVisibleWindows { showHome() }
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        // Opening StickerSense again (Dock, Spotlight, Finder) brings up its main window.
        if !flag { showHome() }
        return true
    }

    /// True when macOS started us at login: stay quietly in the menu bar then.
    private var launchedAtLogin = false

    func applicationWillFinishLaunching(_ notification: Notification) {
        let event = NSAppleEventManager.shared().currentAppleEvent
        launchedAtLogin = event?.eventID == AEEventID(kAEOpenApplication)
            && event?.paramDescriptor(forKeyword: AEKeyword(keyAEPropData))?.enumCodeValue == OSType(keyAELaunchedAsLogInItem)
    }

    // MARK: Hotkeys

    private func registerHotkeys() {
        hotkeys.unregisterAll()
        var failed: [String] = []
        if !hotkeys.register(settings.describeCombo, handler: { [weak self] in self?.describePressed() }) { failed.append(settings.describeCombo.spokenName) }
        if !hotkeys.register(settings.pickerCombo, handler: { [weak self] in self?.showPicker() }) { failed.append(settings.pickerCombo.spokenName) }
        if !hotkeys.register(settings.historyCombo, handler: { [weak self] in self?.showHistory() }) { failed.append(settings.historyCombo.spokenName) }
        if !hotkeys.register(settings.autoToggleCombo, handler: { [weak self] in self?.toggleAuto() }) { failed.append(settings.autoToggleCombo.spokenName) }
        if !failed.isEmpty {
            let message = "StickerSense couldn't use the shortcut \(failed.joined(separator: " and ")) because another app already uses it. Choose a different key in StickerSense Settings."
            log.add(message)
            announcer.say(message, interrupt: false)
        }
        rebuildMenu()
    }

    private func describePressed() {
        model.noteUserActivity()
        controller.shortcutPressed()
    }

    private func toggleAuto() {
        settings.autoDescribe.toggle()
        earcons.play(.tick)
        announcer.say(settings.autoDescribe ? "Automatic sticker descriptions on." : "Automatic sticker descriptions off.")
        rebuildMenu()
    }

    // MARK: Windows

    private func applyColonTrigger() {
        colonTrigger.isEnabled = settings.colonTrigger
        colonTrigger.scope = settings.autoScope
        if settings.colonTrigger { colonTrigger.start() } else { colonTrigger.stop() }
    }

    /// Set when "::" asks for the picker, consumed by the hotkey handler.
    private var pendingTypedRequest: (app: NSRunningApplication, at: Date)?

    func showPicker() {
        if let request = pendingTypedRequest, Date().timeIntervalSince(request.at) < 1.5 {
            pendingTypedRequest = nil
            showPicker(from: request.app, replacing: 2)
            return
        }
        showPicker(from: NSWorkspace.shared.frontmostApplication, replacing: 0)
    }

    /// macOS only lets an app take focus in response to input it received. Typed colons go to
    /// the chat app, not to us, so we press our own picker shortcut: that input *is* ours, and
    /// the picker comes to the front with the search field focused. Falls back to opening it
    /// directly if the shortcut didn't arrive.
    private func openPickerFromTyping(in app: NSRunningApplication) {
        pendingTypedRequest = (app, Date())
        let combo = settings.pickerCombo
        let source = CGEventSource(stateID: .privateState)
        let flags = CGEventFlags([.maskAlternate, .maskShift, .maskCommand])
        let down = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(combo.keyCode), keyDown: true)
        let up = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(combo.keyCode), keyDown: false)
        down?.flags = flags
        up?.flags = flags
        down?.post(tap: .cghidEventTap)
        up?.post(tap: .cghidEventTap)
        Task {
            try? await Task.sleep(for: .milliseconds(500))
            if let request = self.pendingTypedRequest, request.app == app {
                self.log.add("Picker shortcut didn't arrive; opening directly")
                self.pendingTypedRequest = nil
                self.showPicker(from: app, replacing: 2)
            }
        }
    }

    /// - Parameter replacing: characters the paste should replace (the "::" that opened it).
    func showPicker(from target: NSRunningApplication?, replacing: Int) {
        model.noteUserActivity()
        colonTrigger.start() // in case Accessibility was granted since launch
        let pickerModel = PickerModel(app: model)
        pickerModel.charactersToReplace = replacing
        pickerModel.targetApp = target?.bundleIdentifier == Bundle.main.bundleIdentifier ? nil : target
        pickerModel.close = { [weak self] in
            pickerModel.stop()
            self?.windows.close("picker")
            if let target = pickerModel.targetApp { target.activate() }
        }
        windows.show(id: "picker", title: "Sticker Picker", size: CGSize(width: 640, height: 520)) {
            PickerView(model: pickerModel)
        }
    }

    func showHistory() {
        model.noteUserActivity()
        let historyModel = HistoryModel(app: model)
        windows.show(id: "history", title: "Sticker History", size: CGSize(width: 600, height: 480)) {
            HistoryView(model: historyModel)
        }
    }

    @objc func showSettingsWindow() {
        windows.show(id: "settings", title: "StickerSense Settings", size: CGSize(width: 600, height: 680)) {
            SettingsView(settings: self.settings, app: self.model, openSetup: { [weak self] in self?.showSetup() }, openLog: { [weak self] in self?.openLog() })
        }
    }

    @objc func showHome() {
        windows.show(id: "home", title: "StickerSense", size: CGSize(width: 520, height: 560)) {
            HomeView(settings: self.settings, app: self.model, actions: HomeView.Actions(
                picker: { [weak self] in self?.showPicker() },
                history: { [weak self] in self?.showHistory() },
                settings: { [weak self] in self?.showSettingsWindow() },
                setup: { [weak self] in self?.showSetup() },
                help: { [weak self] in self?.menuHelp() }
            ))
        }
    }

    func showSetup() {
        let setupModel = SetupModel(app: model)
        windows.show(id: "setup", title: "StickerSense Setup", size: CGSize(width: 640, height: 700)) {
            SetupView(model: setupModel, describeKeyName: self.settings.describeCombo.spokenName) { [weak self] in
                self?.settings.hasCompletedSetup = true
                self?.windows.close("setup")
            }
        }
    }

    private func openLog() {
        let url = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0].appendingPathComponent("Logs/StickerSense.log")
        NSWorkspace.shared.open(url)
    }

    // MARK: Menus

    private func installStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = item.button {
            button.image = NSImage(systemSymbolName: "face.smiling.inverse", accessibilityDescription: "StickerSense")
            button.setAccessibilityLabel("StickerSense")
        }
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu
        statusItem = item
        rebuildMenu()
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        rebuildMenu()
    }

    private func rebuildMenu() {
        guard let menu = statusItem?.menu else { return }
        menu.removeAllItems()
        menu.addItem(menuItem("Describe What VoiceOver Is On (\(settings.describeCombo.symbolName))", #selector(menuDescribe)))
        menu.addItem(menuItem("Sticker Picker… (\(settings.pickerCombo.symbolName))", #selector(menuPicker)))
        menu.addItem(menuItem("Sticker History… (\(settings.historyCombo.symbolName))", #selector(menuHistory)))
        menu.addItem(.separator())
        let auto = menuItem("Describe Stickers Automatically (\(settings.autoToggleCombo.symbolName))", #selector(menuToggleAuto))
        auto.state = settings.autoDescribe ? .on : .off
        menu.addItem(auto)
        autoMenuItem = auto
        if !model.engineStatus.isReady {
            let warning = NSMenuItem(title: model.engineStatus.explanation, action: nil, keyEquivalent: "")
            warning.isEnabled = false
            menu.addItem(warning)
        }
        menu.addItem(.separator())
        menu.addItem(menuItem("What Can StickerSense Do?", #selector(menuHelp)))
        menu.addItem(menuItem("Settings…", #selector(showSettingsWindow), key: ","))
        menu.addItem(menuItem("Setup and Permissions…", #selector(menuSetup)))
        menu.addItem(.separator())
        menu.addItem(menuItem("Quit StickerSense", #selector(NSApplication.terminate(_:)), key: "q", target: NSApp))
    }

    private func menuItem(_ title: String, _ action: Selector, key: String = "", target: AnyObject? = nil) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = target ?? self
        return item
    }

    @objc private func menuDescribe() {
        // Give the menu a moment to close so the VoiceOver cursor returns to the app.
        Task {
            try? await Task.sleep(for: .milliseconds(600))
            describePressed()
        }
    }
    @objc private func menuPicker() { showPicker() }
    @objc private func menuHistory() { showHistory() }
    @objc private func menuToggleAuto() { toggleAuto() }
    @objc private func menuSetup() { showSetup() }

    /// Speaks the shortcuts — there's no way to glance at them without sight.
    static let donationURL = URL(string: "https://paypal.me/theblindiephoenix")!

    @objc func openDonation() {
        NSWorkspace.shared.open(Self.donationURL)
    }

    @objc func menuHelp() {
        let text = """
        StickerSense shortcuts. \(settings.describeCombo.spokenName): describe what the VoiceOver cursor is on; press it again within 3 seconds for more detail. \
        \(settings.pickerCombo.spokenName): sticker picker, to find and paste a sticker. \
        \(settings.colonTrigger ? "Or type two colons in a chat to open the picker. " : "")\
        \(settings.historyCombo.spokenName): history of described stickers. \
        \(settings.autoToggleCombo.spokenName): turn automatic descriptions \(settings.autoDescribe ? "off" : "on"). \
        Automatic descriptions are \(settings.autoDescribe ? "on" : "off"): stickers and GIFs in chats are described when the VoiceOver cursor rests on them.
        """
        Task {
            try? await Task.sleep(for: .milliseconds(600))
            announcer.say(text)
        }
    }

    /// The menu bar StickerSense shows while one of its windows is in front.
    private func installMainMenu() {
        let main = NSMenu()

        let appMenu = NSMenu(title: "StickerSense")
        appMenu.addItem(withTitle: "About StickerSense", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(menuItem("Settings…", #selector(showSettingsWindow), key: ","))
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Hide StickerSense", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        let hideOthers = appMenu.addItem(withTitle: "Hide Others", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h")
        hideOthers.keyEquivalentModifierMask = [.command, .option]
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Quit StickerSense", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        main.addItem(submenu: appMenu)

        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        main.addItem(submenu: edit)

        let stickers = NSMenu(title: "Stickers")
        stickers.addItem(menuItem("StickerSense Main Window", #selector(showHome), key: "0"))
        stickers.addItem(menuItem("Sticker Picker…", #selector(menuPicker), key: "p"))
        stickers.addItem(menuItem("Sticker History…", #selector(menuHistory), key: "y"))
        stickers.addItem(.separator())
        stickers.addItem(menuItem("Describe Stickers Automatically", #selector(menuToggleAuto)))
        stickers.addItem(menuItem("Setup and Permissions…", #selector(menuSetup)))
        main.addItem(submenu: stickers)

        let window = NSMenu(title: "Window")
        window.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        window.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        window.addItem(withTitle: "Zoom", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        window.addItem(.separator())
        window.addItem(withTitle: "Bring All to Front", action: #selector(NSApplication.arrangeInFront(_:)), keyEquivalent: "")
        main.addItem(submenu: window)
        NSApp.windowsMenu = window

        let help = NSMenu(title: "Help")
        help.addItem(menuItem("What Can StickerSense Do?", #selector(menuHelp), key: "?"))
        help.addItem(.separator())
        help.addItem(menuItem("Support StickerSense (Donate)…", #selector(openDonation)))
        main.addItem(submenu: help)
        NSApp.helpMenu = help

        NSApp.mainMenu = main
    }

    /// Ticks "Describe Stickers Automatically" in the Stickers menu.
    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        if menuItem.action == #selector(menuToggleAuto) { menuItem.state = settings.autoDescribe ? .on : .off }
        return true
    }
}

/// Hosts SwiftUI views in ordinary AppKit windows. While any of them is open, StickerSense is
/// a regular app — in the Dock and Command-Tab, with its own menu bar — so the user can switch
/// away and back like any other app. When the last one closes, it goes back to being a quiet
/// menu bar app.
@MainActor
final class WindowManager: NSObject, NSWindowDelegate {
    private var windows: [String: NSWindow] = [:]

    var hasVisibleWindows: Bool { windows.values.contains { $0.isVisible } }

    func show<Content: View>(id: String, title: String, size: CGSize, @ViewBuilder content: () -> Content) {
        let window: NSWindow
        if let existing = windows[id] {
            window = existing
            window.contentViewController = NSHostingController(rootView: content())
        } else {
            window = NSWindow(
                contentRect: NSRect(origin: .zero, size: size),
                styleMask: [.titled, .closable, .resizable, .miniaturizable],
                backing: .buffered, defer: false
            )
            window.title = title
            window.isReleasedWhenClosed = false
            window.delegate = self
            window.contentViewController = NSHostingController(rootView: content())
            window.setContentSize(size)
            window.center()
            window.setFrameAutosaveName("StickerSense.\(id)")
            windows[id] = window
        }
        if NSApp.activationPolicy() != .regular {
            NSApp.setActivationPolicy(.regular)
        }
        NSApp.activate()
        window.makeKeyAndOrderFront(nil)
    }

    func close(_ id: String) {
        windows[id]?.close()
    }

    func windowWillClose(_ notification: Notification) {
        // After the window is gone: back to a menu bar app if it was the last one.
        DispatchQueue.main.async {
            if !self.hasVisibleWindows, NSApp.activationPolicy() == .regular {
                NSApp.setActivationPolicy(.accessory)
            }
        }
    }
}

private extension NSMenu {
    func addItem(submenu: NSMenu) {
        let item = NSMenuItem(title: submenu.title, action: nil, keyEquivalent: "")
        item.submenu = submenu
        addItem(item)
    }
}
