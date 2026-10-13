<#
.SYNOPSIS
    End-to-end test of an installed StickerSense on a real Windows desktop.

.DESCRIPTION
    Run it in the signed-in user's desktop session, unlocked (not over SSH: windows, focus and key
    presses only exist on the interactive desktop, and Windows refuses key presses while locked). It drives StickerSense the way a user would, through
    UI Automation and synthesized key presses, and reads the results from StickerSense's log
    (%LOCALAPPDATA%\StickerSense\StickerSense.log):

      1. Starts StickerSense with --watch-app StickerSense (so its own test chat counts as a
         chat app) and checks: it launched, its shortcuts are registered, it has a notification
         area icon, and its first window (Setup on first run) is a normal taskbar window.
      2. Finishes Setup if it's showing, then starts StickerSense again and checks the running
         copy brings up its main window. Closes the main window and checks StickerSense keeps running.
      3. Waits for the model to load, opens the test chat (--test-window), and on its sticker:
         turns automatic descriptions off (Ctrl+Alt+Shift+A), presses Ctrl+Alt+Shift+D (short
         description), presses it again within 3 seconds (detail), turns automatic descriptions
         back on and moves focus onto the sticker (automatic description).
      4. Types "::" in the test chat's message box and checks the sticker picker opens with focus
         in its search box, types a word, presses Escape and checks the colons are still there.
         Then does it again and presses Enter to paste the first result: the colons are removed.
      5. Closes the test chat and quits StickerSense (StickerSense.exe --quit).

    Output is plain text, then a JSON summary (also written to -Out). Exit code 0 when every
    check passed. The settings and library in %LOCALAPPDATA%\StickerSense are the real ones, so
    the test sticker ends up in History; remove it there afterwards if you like.

.PARAMETER Exe
    StickerSense.exe to test. Default: the per-user install, %LOCALAPPDATA%\Programs\StickerSense.

.PARAMETER Out
    Also write the JSON summary to this file.

.PARAMETER ModelTimeoutSeconds
    How long to wait for the model to load. Default 300.
#>
[CmdletBinding()]
param(
    [string] $Exe = "$env:LOCALAPPDATA\Programs\StickerSense\StickerSense.exe",
    [string] $Out,
    [int] $ModelTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class E2E
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier id, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short VkKeyScanW(char c);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint type);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);

    /// <summary>Locked: a secure desktop has the input, or the lock screen (LockApp) is in front. Key presses can't be sent then.</summary>
    public static bool IsLocked()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return true;
        var name = new StringBuilder(64); int needed;
        GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed);
        CloseDesktop(desktop);
        if (!string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase)) return true;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        try { return System.Diagnostics.Process.GetProcessById(ProcessOf(fg)).ProcessName.Equals("LockApp", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput mi; [FieldOffset(0)] public KeyboardInput ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    public class WindowInfo
    {
        public long Handle; public int ProcessId; public string Title; public string ClassName;
        public bool Visible, Owned, ToolWindow, AppWindow, Topmost, Cloaked;
        /// <summary>The rule Windows uses for taskbar buttons and Alt+Tab.</summary>
        public bool InTaskbarAndAltTab { get { return Visible && !Cloaked && !ToolWindow && (!Owned || AppWindow); } }
    }

    public static List<WindowInfo> Windows(int processId)
    {
        var found = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid != (uint)processId) return true;
            var title = new StringBuilder(512); GetWindowText(hwnd, title, title.Capacity);
            var cls = new StringBuilder(256); GetClassName(hwnd, cls, cls.Capacity);
            long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
            int cloaked;
            if (DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) != 0) cloaked = 0;
            found.Add(new WindowInfo
            {
                Handle = hwnd.ToInt64(), ProcessId = processId, Title = title.ToString(), ClassName = cls.ToString(),
                Visible = IsWindowVisible(hwnd), Owned = GetWindow(hwnd, 4) != IntPtr.Zero,
                ToolWindow = (ex & 0x80) != 0, AppWindow = (ex & 0x40000) != 0, Topmost = (ex & 0x8) != 0, Cloaked = cloaked != 0,
            });
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static int ProcessOf(IntPtr hwnd)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        return (int)pid;
    }

    /// <summary>Finds a notification area icon of the process (WinForms numbers its icons from 1).</summary>
    public static string TrayIcon(int processId)
    {
        foreach (var window in Windows(processId))
        {
            if (!window.ClassName.StartsWith("WindowsForms10.Window")) continue;
            for (uint id = 1; id <= 4; id++)
            {
                var identifier = new NotifyIconIdentifier { hWnd = new IntPtr(window.Handle), uID = id };
                identifier.cbSize = (uint)Marshal.SizeOf(identifier);
                Rect rect;
                if (Shell_NotifyIconGetRect(ref identifier, out rect) == 0)
                    return string.Format("icon {0} at {1},{2} size {3}x{4}", id, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            }
        }
        return null;
    }

    private static Input Key(ushort vk, bool up)
    {
        var input = new Input { type = 1 };
        input.u.ki = new KeyboardInput { wVk = vk, wScan = (ushort)MapVirtualKeyW(vk, 0), dwFlags = up ? 2u : 0u };
        return input;
    }

    private static void Send(List<Input> inputs)
    {
        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Input)));
        if (sent != inputs.Count) throw new InvalidOperationException("SendInput sent " + sent + " of " + inputs.Count + " events, error " + Marshal.GetLastWin32Error());
    }

    /// <summary>Presses the keys in order and releases them in reverse, like a shortcut.</summary>
    public static void Chord(params ushort[] keys)
    {
        var inputs = new List<Input>();
        foreach (var k in keys) inputs.Add(Key(k, false));
        for (int i = keys.Length - 1; i >= 0; i--) inputs.Add(Key(keys[i], true));
        Send(inputs);
    }

    /// <summary>Types a character with its real key (and Shift) on the current keyboard layout, like a person.</summary>
    public static void TypeKey(char c)
    {
        short scan = VkKeyScanW(c);
        if (scan == -1) throw new InvalidOperationException("No key for " + c);
        var vk = (ushort)(scan & 0xff);
        var shift = (scan & 0x100) != 0;
        var inputs = new List<Input>();
        if (shift) inputs.Add(Key(0x10, false));
        inputs.Add(Key(vk, false));
        inputs.Add(Key(vk, true));
        if (shift) inputs.Add(Key(0x10, true));
        Send(inputs);
    }

    /// <summary>Types text as Unicode characters.</summary>
    public static void Text(string text)
    {
        var inputs = new List<Input>();
        foreach (var c in text)
        {
            var down = new Input { type = 1 }; down.u.ki = new KeyboardInput { wScan = c, dwFlags = 4 };
            var up = new Input { type = 1 }; up.u.ki = new KeyboardInput { wScan = c, dwFlags = 4 | 2 };
            inputs.Add(down); inputs.Add(up);
        }
        Send(inputs);
    }
}
'@

$VK_CONTROL = 0x11; $VK_MENU = 0x12; $VK_SHIFT = 0x10; $VK_ESCAPE = 0x1B; $VK_RETURN = 0x0D
$UIA = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$NameProperty = [System.Windows.Automation.AutomationElement]::NameProperty

$results = [ordered]@{}
$failures = New-Object System.Collections.Generic.List[string]
$started = Get-Date

function Say([string] $message) { Write-Output ("{0,6:0.0}s  {1}" -f ((Get-Date) - $started).TotalSeconds, $message) }
function Check([string] $key, [bool] $ok, $detail) {
    $results[$key] = [ordered]@{ ok = $ok; detail = $detail }
    if ($ok) { Say "PASS $key  $detail" } else { Say "FAIL $key  $detail"; $failures.Add($key) }
}

$logPath = Join-Path $env:LOCALAPPDATA 'StickerSense\StickerSense.log'
function LogLength { if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 } }
function LogSince([long] $offset) {
    if (-not (Test-Path $logPath)) { return '' }
    $stream = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite, Delete')
    try {
        if ($offset -gt $stream.Length) { $offset = 0 }   # the log was started afresh
        [void] $stream.Seek($offset, 'Begin')
        return (New-Object IO.StreamReader($stream)).ReadToEnd()
    }
    finally { $stream.Dispose() }
}
# Waits for a log line matching the pattern, written after the offset; returns the line or $null.
# Gives up early if the process given exits.
function WaitLog([long] $offset, [string] $pattern, [double] $seconds, $process = $null) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $line = (LogSince $offset) -split "`r?`n" | Where-Object { $_ -match $pattern } | Select-Object -First 1
        if ($line) { return $line }
        if ($process -and $process.HasExited) { return $null }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $null
}
function LogLines([long] $offset, [string] $pattern) { (LogSince $offset) -split "`r?`n" | Where-Object { $_ -match $pattern } }

function FindWindow([string] $title, [double] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $condition = New-Object System.Windows.Automation.PropertyCondition($NameProperty, $title)
    do {
        $window = $UIA::RootElement.FindFirst($Scope::Children, $condition)
        if ($window) { return $window }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $null
}
function FindIn($parent, [string] $name, [double] $seconds = 5) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $condition = New-Object System.Windows.Automation.PropertyCondition($NameProperty, $name)
    do {
        $element = $parent.FindFirst($Scope::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $null
}
function WaitGone([string] $title, [double] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $condition = New-Object System.Windows.Automation.PropertyCondition($NameProperty, $title)
    do {
        if (-not $UIA::RootElement.FindFirst($Scope::Children, $condition)) { return $true }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $false
}
function Focused { try { $UIA::FocusedElement } catch { $null } }
function Describe($element) {
    if (-not $element) { return '(nothing)' }
    try { "{0} '{1}' in process {2}" -f $element.Current.ControlType.ProgrammaticName, $element.Current.Name, $element.Current.ProcessId } catch { '(gone)' }
}
function ValueOf($element) {
    try { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { $null }
}
function SetValue($element, [string] $value) {
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}
function Front($window) {
    $handle = [IntPtr] $window.Current.NativeWindowHandle
    [void] [E2E]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 300
    return [E2E]::GetForegroundWindow() -eq $handle
}

if (-not (Test-Path $Exe)) { throw "StickerSense isn't installed at $Exe" }
$appProcess = $null; $chatProcess = $null
try {
    # 1. Start StickerSense.
    $running = @(Get-Process StickerSense -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Say "StickerSense is already running; quitting it first."
        & $Exe --quit | Out-Null
        Start-Sleep -Seconds 2
        Get-Process StickerSense -ErrorAction SilentlyContinue | Stop-Process -Force
    }
    $mark = LogLength
    Say "Starting $Exe --watch-app StickerSense"
    $appProcess = Start-Process -FilePath $Exe -ArgumentList '--watch-app', 'StickerSense' -PassThru
    $launched = WaitLog $mark 'Launched\.' 60
    Check 'launched' ($null -ne $launched) $launched
    $shortcuts = WaitLog $mark 'Shortcuts registered|couldn''t use the shortcut' 10
    Check 'shortcutsRegistered' ($shortcuts -match 'Shortcuts registered: Control Alt Shift D, Control Alt Shift S, Control Alt Shift H, Control Alt Shift A\.') $shortcuts
    $tray = [E2E]::TrayIcon($appProcess.Id)
    Check 'trayIcon' ($null -ne $tray) $tray

    $first = $null
    $deadline = (Get-Date).AddSeconds(15)
    while (-not $first -and (Get-Date) -lt $deadline) {
        $first = [E2E]::Windows($appProcess.Id) | Where-Object { $_.Visible -and $_.Title -like 'StickerSense*' -and $_.Title -ne 'StickerSense announcements' } | Select-Object -First 1
        if (-not $first) { Start-Sleep -Milliseconds 250 }
    }
    Check 'firstWindowIsNormal' ($first -and $first.InTaskbarAndAltTab -and -not $first.Topmost) ($first | Select-Object Title, InTaskbarAndAltTab, Topmost, Owned, ToolWindow | ConvertTo-Json -Compress)

    # 2. Setup (first run), then the second-instance path to the main window.
    $setup = FindWindow 'StickerSense Setup' 1
    if ($setup) {
        $statuses = @{}
        $edits = $setup.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
        foreach ($edit in $edits) { $statuses[$edit.Current.Name] = ValueOf $edit }
        $results['setupStatuses'] = $statuses
        foreach ($k in $statuses.Keys) { Say "  Setup: $k = $($statuses[$k])" }
        $done = FindIn $setup 'Done' 2
        if (-not $done) { $done = FindIn $setup 'Finish Later' 2 }
        $done.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Check 'setupClosed' (WaitGone 'StickerSense Setup' 5) "pressed $($done.Current.Name)"
    }
    $second = Start-Process -FilePath $Exe -PassThru
    $secondExited = $second.WaitForExit(10000)
    $main = FindWindow 'StickerSense' 10
    $mainInfo = $null
    if ($main) { $mainInfo = [E2E]::Windows($appProcess.Id) | Where-Object { $_.Handle -eq $main.Current.NativeWindowHandle } }
    Check 'secondStartShowsMainWindow' ($secondExited -and $main -and $main.Current.ProcessId -eq $appProcess.Id -and $mainInfo.InTaskbarAndAltTab -and -not $mainInfo.Topmost) ("second copy exited: $secondExited; main window: " + ($mainInfo | Select-Object Title, InTaskbarAndAltTab, Topmost | ConvertTo-Json -Compress))
    if ($main) {
        $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        $closed = WaitGone 'StickerSense' 5
        Start-Sleep -Milliseconds 500
        Check 'keepsRunningAfterClosingMainWindow' ($closed -and -not $appProcess.HasExited) "window closed: $closed; still running: $(-not $appProcess.HasExited)"
    }

    # 3. Describing, on the test chat's sticker. This needs the desktop: key presses can't be sent to a locked computer.
    $ready = WaitLog $mark 'Model ready on|Model failed to load' $ModelTimeoutSeconds $appProcess
    Check 'modelLoaded' ($ready -match 'Model ready on') $ready
    if ($appProcess.HasExited) { throw "StickerSense exited unexpectedly (exit code $($appProcess.ExitCode))." }
    if ([E2E]::IsLocked()) {
        Check 'unlocked' $false 'Windows is locked, so the shortcut, automatic description and "::" tests were skipped. Unlock the computer and run this again.'
        throw [System.OperationCanceledException]::new('locked')
    }
    $chatProcess = Start-Process -FilePath $Exe -ArgumentList '--test-window' -PassThru
    $chat = FindWindow 'StickerSense test chat' 20
    if (-not $chat) { throw 'The test chat window did not open.' }
    $sticker = FindIn $chat 'Sticker from Test User, 10:00'
    $box = FindIn $chat 'Type a message'
    Check 'testChatInFront' (Front $chat) (Describe $sticker)
    $box.SetFocus()
    Start-Sleep -Milliseconds 300

    $mark = LogLength
    [E2E]::Chord($VK_CONTROL, $VK_MENU, $VK_SHIFT, 0x41)   # A: automatic descriptions off
    $autoOff = WaitLog $mark 'Auto:|automatic' 3
    Start-Sleep -Seconds 2                                  # past the quiet time after switching apps
    $sticker.SetFocus()
    Start-Sleep -Milliseconds 800
    $mark = LogLength
    $pressed = Get-Date
    [E2E]::Chord($VK_CONTROL, $VK_MENU, $VK_SHIFT, 0x44)   # D
    $short = WaitLog $mark '^\S+ \S+ Short in ' 120
    $shortWall = ((Get-Date) - $pressed).TotalSeconds
    Check 'shortcutDescribes' ($null -ne $short) ("{0}  (wall {1:0.0}s)" -f $short, $shortWall)
    $results['shortcutLog'] = @(LogLines $mark '.')
    $mark = LogLength
    $pressed = Get-Date
    [E2E]::Chord($VK_CONTROL, $VK_MENU, $VK_SHIFT, 0x44)   # D again within 3 s: detail
    $detail = WaitLog $mark 'Detail in |Detail cancelled|Error:' 180
    $detailWall = ((Get-Date) - $pressed).TotalSeconds
    Check 'secondPressGivesDetail' ($detail -match 'Detail in ') ("{0}  (wall {1:0.0}s)" -f $detail, $detailWall)
    $results['detailLog'] = @(LogLines $mark '.')

    Start-Sleep -Seconds 1
    $box.SetFocus()
    $mark = LogLength
    [E2E]::Chord($VK_CONTROL, $VK_MENU, $VK_SHIFT, 0x41)   # A: automatic descriptions on
    Start-Sleep -Seconds 2
    $mark = LogLength
    $moved = Get-Date
    $sticker.SetFocus()
    $auto = WaitLog $mark '^\S+ \S+ Auto in |Auto: (skipped|failed|focus moved)' 120
    $autoWall = ((Get-Date) - $moved).TotalSeconds
    Check 'autoDescribesOnFocus' ($auto -match 'Auto in ') ("{0}  (wall {1:0.0}s)" -f $auto, $autoWall)
    $results['autoLog'] = @(LogLines $mark '.')

    # 4. "::" opens the picker; Escape keeps the colons; Enter replaces them with the sticker.
    Start-Sleep -Seconds 1
    $box.SetFocus()
    SetValue $box ''
    Start-Sleep -Milliseconds 500
    $mark = LogLength
    [E2E]::TypeKey(':'); Start-Sleep -Milliseconds 120; [E2E]::TypeKey(':')
    $picker = FindWindow 'Sticker Picker' 6
    $opened = WaitLog $mark '"::" typed' 1
    Start-Sleep -Milliseconds 500
    $focus = Focused
    Check 'colonsOpenPicker' ($picker -and $focus -and $focus.Current.Name -eq 'Search stickers' -and $focus.Current.ProcessId -eq $appProcess.Id) ("$opened; focus: " + (Describe $focus))
    if ($picker) {
        [E2E]::Text('happy')
        Start-Sleep -Seconds 2
        $list = FindIn $picker 'Stickers' 2
        $items = @()
        if ($list) { $items = @($list.FindAll($Scope::Children, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) }
        $search = FindIn $picker 'Search stickers' 1
        $results['pickerSearch'] = [ordered]@{ typed = (ValueOf $search); results = $items }
        Say "  Picker search '$(ValueOf $search)': $($items -join ' | ')"
        [E2E]::Chord($VK_ESCAPE)
        $gone = WaitGone 'Sticker Picker' 3
        Start-Sleep -Milliseconds 500
        $value = ValueOf $box
        $focus = Focused
        Check 'escapeKeepsColons' ($gone -and $value -eq '::') ("picker closed: $gone; message box: '$value'; focus: " + (Describe $focus))

        $box.SetFocus()
        Start-Sleep -Milliseconds 300
        $mark = LogLength
        [E2E]::TypeKey(':'); Start-Sleep -Milliseconds 120; [E2E]::TypeKey(':')
        $picker = FindWindow 'Sticker Picker' 6
        if ($picker) {
            Start-Sleep -Milliseconds 500
            [E2E]::Text('happy')
            Start-Sleep -Seconds 2
            [E2E]::Chord($VK_RETURN)
            $pasted = WaitLog $mark 'Picker: ' 10
            Start-Sleep -Milliseconds 800
            $value = ValueOf $box
            Check 'enterReplacesColons' ($pasted -and $value -eq '::') ("$pasted; message box: '$value' (the box held '::::' before; a text box can't show a pasted image)")
        }
        else { Check 'enterReplacesColons' $false 'the picker did not open the second time' }
    }

    $results['speechRoutes'] = @(LogLines 0 'Speaking through' | Select-Object -Last 5)
}
catch [System.OperationCanceledException] {
}
catch {
    Check 'error' $false ($_ | Out-String)
}
finally {
    # 5. Close everything this test opened.
    if ($chatProcess -and -not $chatProcess.HasExited) {
        [void] $chatProcess.CloseMainWindow()
        if (-not $chatProcess.WaitForExit(5000)) { $chatProcess.Kill() }
    }
    if ($appProcess -and -not $appProcess.HasExited) {
        $quit = Start-Process -FilePath $Exe -ArgumentList '--quit' -PassThru -Wait
        $exited = $appProcess.WaitForExit(15000)
        Check 'quitCommand' ($exited -and $quit.ExitCode -eq 0) "exit code $($quit.ExitCode); StickerSense exited: $exited"
        if (-not $exited) { $appProcess.Kill() }
    }
}

$results['failures'] = @($failures)
$json = $results | ConvertTo-Json -Depth 6
Write-Output $json
if ($Out) { Set-Content -Path $Out -Value $json -Encoding UTF8 }
if ($failures.Count -gt 0) { exit 1 } else { exit 0 }
