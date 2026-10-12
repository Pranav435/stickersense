using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using StickerSense.App.Platform;

namespace StickerSense.App.Speech;

internal enum ScreenReaderKind
{
    None,
    Nvda,
    Jaws,
    Narrator,
    /// <summary>Windows says a screen reader is running, but not one we recognise.</summary>
    Other,
}

internal static class ScreenReaders
{
    /// <summary>Windows' own "a screen reader is running" flag (SPI_GETSCREENREADER).</summary>
    public static bool SystemFlag =>
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETSCREENREADER, 0, out var running, 0) && running != 0;

    /// <summary>Which screen reader is running, from the process list only (a local call; screen readers aren't asked).</summary>
    public static ScreenReaderKind Detect()
    {
        if (IsProcessRunning("nvda")) return ScreenReaderKind.Nvda;
        if (IsProcessRunning("jfw")) return ScreenReaderKind.Jaws;
        if (IsProcessRunning("Narrator")) return ScreenReaderKind.Narrator;
        return SystemFlag ? ScreenReaderKind.Other : ScreenReaderKind.None;
    }

    public static string DisplayName(ScreenReaderKind kind) => kind switch
    {
        ScreenReaderKind.Nvda => "NVDA",
        ScreenReaderKind.Jaws => "JAWS",
        ScreenReaderKind.Narrator => "Narrator",
        ScreenReaderKind.Other => "a screen reader",
        _ => "no screen reader",
    };

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }
}

/// <summary>
/// NVDA's controller client (nvdaControllerClient.dll from NVDA's developer downloads), loaded
/// only if the DLL sits next to StickerSense.exe. It isn't shipped with StickerSense; see the README.
/// </summary>
internal sealed unsafe class NvdaController
{
    private readonly delegate* unmanaged[Stdcall]<char*, int> speakText;
    private readonly delegate* unmanaged[Stdcall]<char*, int> brailleMessage;
    private readonly delegate* unmanaged[Stdcall]<int> cancelSpeech;

    private NvdaController() { }

    private NvdaController(IntPtr library, string path)
    {
        speakText = (delegate* unmanaged[Stdcall]<char*, int>)NativeLibrary.GetExport(library, "nvdaController_speakText");
        brailleMessage = (delegate* unmanaged[Stdcall]<char*, int>)NativeLibrary.GetExport(library, "nvdaController_brailleMessage");
        cancelSpeech = (delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(library, "nvdaController_cancelSpeech");
        LoadedFrom = path;
    }

    /// <summary>Full path of the loaded DLL, or null when it wasn't found / couldn't load.</summary>
    public string? LoadedFrom { get; }

    public bool IsLoaded => LoadedFrom is not null;

    public static readonly string[] FileNames = ["nvdaControllerClient.dll", "nvdaControllerClient64.dll"];

    public static NvdaController Load(string directory)
    {
        foreach (var name in FileNames)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var library)) continue;
            try
            {
                return new NvdaController(library, path);
            }
            catch (EntryPointNotFoundException)
            {
                NativeLibrary.Free(library);
            }
        }
        return new NvdaController();
    }

    /// <summary>
    /// Speaks through NVDA (in the user's voice and rate) and flashes the text on the braille
    /// display. Fire and forget; false only when the call itself fails (NVDA not reachable).
    /// </summary>
    public bool Speak(string text, bool interrupt)
    {
        if (!IsLoaded) return false;
        try
        {
            if (interrupt) cancelSpeech();
            fixed (char* p = text)
            {
                var spoken = speakText(p) == 0;
                brailleMessage(p);
                return spoken;
            }
        }
        catch (SEHException)
        {
            return false;
        }
    }
}

/// <summary>JAWS through its late-bound COM API (FreedomSci.JawsApi), which JAWS registers itself.</summary>
internal sealed class JawsApi
{
    private object? api;

    public bool Say(string text, bool interrupt)
    {
        try
        {
            if (api is null)
            {
                var type = Type.GetTypeFromProgID("FreedomSci.JawsApi");
                if (type is null) return false;
                api = Activator.CreateInstance(type);
            }
            dynamic jaws = api!;
            bool spoken = jaws.SayString(text, interrupt);
            try
            {
                // Also show it on the braille display.
                var safe = text.Replace('"', '\'').Replace('\\', '/');
                jaws.RunFunction($"BrailleMessage(\"{safe}\")");
            }
            catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
            }
            return spoken;
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or TypeLoadException)
        {
            api = null; // JAWS restarted or isn't there; try again next time
            return false;
        }
    }
}
