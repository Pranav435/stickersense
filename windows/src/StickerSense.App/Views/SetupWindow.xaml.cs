using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using StickerSense.App.Platform;
using StickerSense.App.Speech;
using StickerSense.Core;

namespace StickerSense.App.Views;

internal enum CheckState
{
    Done,
    NeedsAttention,
    Optional,
}

/// <summary>One checklist line: what the screen reader says as the name, and the explanation as the text.</summary>
internal sealed class CheckRow(string title) : INotifyPropertyChanged
{
    private CheckState state = CheckState.NeedsAttention;
    private string text = "Checking…";

    public string Title { get; } = title;

    public string Text => text;

    public string AccessibleName => $"{Title}: {state switch
    {
        CheckState.Done => "done",
        CheckState.Optional => "optional",
        _ => "needs attention",
    }}";

    public bool IsDone => state == CheckState.Done;

    public void Update(CheckState newState, string newText)
    {
        if (state == newState && text == newText) return;
        state = newState;
        text = newText;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class SetupViewModel : INotifyPropertyChanged
{
    private readonly AppServices app;
    private readonly PercentMilestones milestones = new();
    private readonly GpuInfo.Adapter? gpu = GpuInfo.BestHardwareAdapter();
    private CancellationTokenSource? download;
    private int downloadPercent;
    private bool announceWhenReady;

    public SetupViewModel(AppServices app, string describeShortcut)
    {
        this.app = app;
        DescribeShortcut = describeShortcut;
        app.PropertyChanged += AppChanged;
    }

    public string DescribeShortcut { get; }

    public CheckRow Model { get; } = new("Description model");
    public CheckRow ScreenReader { get; } = new("Screen reader");
    public CheckRow TextRecognition { get; } = new("Text recognition");
    public CheckRow WhatsApp { get; } = new("WhatsApp stickers");
    public CheckRow StickerMaker { get; } = new("Sticker maker");

    public string Intro =>
        $"StickerSense describes stickers, GIFs and pictures for you, on this computer, in any app. Put focus on a sticker and press {DescribeShortcut}. " +
        "Press it again within 3 seconds for more detail. Stickers and GIFs in chats are also described automatically when focus lands on them. " +
        "To send a sticker, type two colons in a chat's message box: the sticker picker opens, and the sticker you pick replaces the colons.";

    public string PracticeText =>
        $"Tab to the practice sticker, then press {DescribeShortcut}. Press it again quickly for the detailed description.";

    public string PracticeHint => $"Press {DescribeShortcut} to hear it described.";

    /// <summary>A graphics card with memory of its own runs Phi-3.5-vision fastest; otherwise the fast processor model.</summary>
    public ModelVariant Recommended => gpu is not null && GpuInfo.IsWorthUsing(gpu) ? ModelVariant.DirectML : ModelVariant.Qwen3VL2B;

    public string FastDownloadTitle =>
        $"Download the _fast model, Qwen3-VL 2B, for the processor ({ModelCatalog.ApproximateSize(ModelVariant.Qwen3VL2B)}){(Recommended == ModelVariant.Qwen3VL2B ? ", recommended" : "")}";

    public string GpuDownloadTitle =>
        $"Download Phi-3.5-vision for the _graphics card ({ModelCatalog.ApproximateSize(ModelVariant.DirectML)}){(Recommended == ModelVariant.DirectML ? ", recommended" : "")}";

    public string CpuDownloadTitle =>
        $"Download Phi-3.5-vision for the _processor ({ModelCatalog.ApproximateSize(ModelVariant.Cpu)})";

    public bool Downloading => download is not null;
    public int DownloadPercent => downloadPercent;
    public bool CanDownload => !Downloading && !app.EngineStatus.CanDescribe;
    public bool CanImport => WhatsAppImporter.IsInstalled;
    public bool CanDownloadStickerMaker => app.StickerDownloadPercent < 0 && !app.IsStickerModelInstalled(app.Settings.StickerModel);
    public bool NeedsNvdaDll => app.Announcer.ScreenReader() == ScreenReaderKind.Nvda && !app.Announcer.Nvda.IsLoaded;
    public string DoneTitle => Model.IsDone ? "_Done" : "_Finish Later";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        RefreshModel();
        RefreshScreenReader();
        TextRecognition.Update(
            app.Ocr.IsAvailable ? CheckState.Done : CheckState.Optional,
            app.Ocr.IsAvailable
                ? $"Windows text recognition is ready ({app.Ocr.LanguageName}), so words on stickers are read exactly."
                : "No Windows text recognition language is installed. Optional: in Windows Settings, Time and language, Language and region, open your language's options and add Optical character recognition.");
        if (!WhatsAppImporter.IsInstalled)
        {
            WhatsApp.Update(CheckState.Optional, "WhatsApp Desktop isn't installed from the Microsoft Store, so there's nothing to import.");
        }
        else if (app.LibraryCount > 0 || app.ImportStatus.Length > 0)
        {
            WhatsApp.Update(CheckState.Done, app.ImportStatus.Length == 0 ? $"Your library has {app.LibraryCount} stickers." : app.ImportStatus);
        }
        else
        {
            WhatsApp.Update(CheckState.Optional, "Optional: import your WhatsApp stickers so you can find and send them from the picker.");
        }
        var stickerModel = app.Settings.StickerModel;
        if (app.IsStickerModelInstalled(stickerModel))
        {
            StickerMaker.Update(CheckState.Done, "The sticker maker is ready: in the picker, type what you want and press Control G to make your own stickers.");
        }
        else if (app.StickerDownloadPercent >= 0)
        {
            StickerMaker.Update(CheckState.Optional, $"Downloading the sticker maker: {app.StickerDownloadPercent} percent.");
        }
        else
        {
            StickerMaker.Update(CheckState.Optional,
                $"Optional: download the sticker maker ({StickerMakerCatalog.SpokenSize(stickerModel)}) now, so the picker can make stickers on this computer. Otherwise it downloads the first time you make one.");
        }
        Changed(nameof(CanDownload), nameof(CanImport), nameof(NeedsNvdaDll), nameof(DoneTitle), nameof(CanDownloadStickerMaker));
    }

    public async Task DownloadStickerMakerAsync()
    {
        var download = app.DownloadStickerModelAsync(app.Settings.StickerModel);
        Refresh();
        await download;
        Refresh();
    }

    private void RefreshModel()
    {
        if (Downloading)
        {
            Model.Update(CheckState.NeedsAttention, $"Downloading the model, {downloadPercent} percent.");
            return;
        }
        var status = app.EngineStatus;
        var recommendation = gpu is null
            ? "This computer has no graphics card StickerSense can use, so get the fast model for the processor."
            : GpuInfo.IsWorthUsing(gpu)
                ? $"Your graphics card ({gpu.Name.Trim()}) can run the graphics card model, which is fastest here."
                : $"Your graphics ({gpu.Name.Trim()}) are built in and share the computer's memory, so a model on the processor is faster here. Get the fast model.";
        switch (status.State)
        {
            case EngineState.Ready:
                Model.Update(CheckState.Done, $"The on-device model{ModelName()} is ready, running on {app.Describer.RunningOn ?? "this computer"}.");
                break;
            case EngineState.NotLoaded:
            case EngineState.Loading:
                Model.Update(CheckState.Done, "The on-device model is installed. It's loading, which takes a little while the first time.");
                break;
            case EngineState.ModelMissing:
                Model.Update(CheckState.NeedsAttention, $"The description model isn't installed yet. Download it below; it stays on this computer. {recommendation}");
                break;
            case EngineState.ModelIncomplete:
                Model.Update(CheckState.NeedsAttention, "The model download didn't finish. Download again to continue where it stopped.");
                break;
            default:
                Model.Update(CheckState.NeedsAttention, $"{status.Explanation} If the graphics card can't run it, choose The processor only in Settings, or download the processor model.");
                break;
        }
    }

    /// <summary>", Qwen3-VL 2B," or similar, for the status line.</summary>
    private string ModelName() =>
        app.Describer.ModelDirectory is { } directory ? $", {ModelCatalog.FamilyName(ModelCatalog.DetectVariant(directory))}," : "";

    private void RefreshScreenReader()
    {
        var kind = app.Announcer.ScreenReader(refresh: true);
        switch (kind)
        {
            case ScreenReaderKind.Nvda when app.Announcer.Nvda.IsLoaded:
                ScreenReader.Update(CheckState.Done, "NVDA is running. StickerSense speaks and brailles through NVDA directly.");
                break;
            case ScreenReaderKind.Nvda:
                ScreenReader.Update(CheckState.NeedsAttention,
                    "NVDA is running, but nvdaControllerClient.dll isn't in the StickerSense folder. NVDA only reads notifications from the app you're in, so StickerSense speaks with the Windows voice instead of through NVDA, and nothing goes to your braille display. Put the DLL from NVDA's controller client download next to StickerSense.exe; the README explains where to get it.");
                break;
            case ScreenReaderKind.Jaws:
                ScreenReader.Update(CheckState.Done, "JAWS is running. StickerSense speaks through JAWS.");
                break;
            case ScreenReaderKind.Narrator:
                ScreenReader.Update(CheckState.Done, "Narrator is running. StickerSense speaks through Narrator notifications.");
                break;
            case ScreenReaderKind.Other:
                ScreenReader.Update(CheckState.Done, "A screen reader is running. StickerSense speaks through Windows notifications.");
                break;
            default:
                ScreenReader.Update(CheckState.Optional, "No screen reader is running, so StickerSense speaks with the Windows voice.");
                break;
        }
    }

    public async Task DownloadAsync(ModelVariant variant)
    {
        if (Downloading) return;
        var run = download = new CancellationTokenSource();
        downloadPercent = 0;
        milestones.Reset();
        Changed(nameof(Downloading), nameof(DownloadPercent), nameof(CanDownload));
        RefreshModel();
        app.Announcer.Say($"Downloading {ModelCatalog.DisplayName(variant)}, about {ModelCatalog.ApproximateSize(variant)}. You can keep using your computer. I'll announce every 10 percent.");
        var progress = new Progress<DownloadProgress>(p =>
        {
            downloadPercent = p.Percent;
            Changed(nameof(DownloadPercent));
            RefreshModel();
            if (milestones.Next(p.Percent) is { } milestone && milestone < 100) app.Announcer.Say($"Model download {milestone} percent.", interrupt: false);
        });
        try
        {
            await app.DownloadModelAsync(variant, progress, run.Token);
            announceWhenReady = true;
            app.Earcons.Play(Earcon.Ready);
            app.Announcer.Say("The model is downloaded. Loading it now.");
        }
        catch (OperationCanceledException)
        {
            app.Announcer.Say("Download stopped. Next time it continues where it left off.");
        }
        catch (Exception e) when (e is ModelDownloadException or IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
        {
            app.Earcons.Play(Earcon.Failed);
            app.Announcer.Say(e is ModelDownloadException ? e.Message : $"The download failed: {e.Message}");
            app.Log.Add($"Model download failed: {e}");
        }
        finally
        {
            download = null;
            run.Dispose();
            Changed(nameof(Downloading), nameof(CanDownload));
            Refresh();
        }
    }

    public void CancelDownload() => download?.Cancel();

    public async Task ImportWhatsAppAsync()
    {
        await app.ImportWhatsAppAsync();
        Refresh();
    }

    private void AppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppServices.StickerDownloadPercent) && app.StickerDownloadPercent % 10 is 0 or -1)
        {
            Refresh();
            return;
        }
        if (e.PropertyName != nameof(AppServices.EngineStatus)) return;
        Refresh();
        if (announceWhenReady && app.EngineStatus.IsReady)
        {
            announceWhenReady = false;
            app.Announcer.Say("The model is ready. Tab to the practice sticker to try it.", interrupt: false);
        }
        else if (announceWhenReady && app.EngineStatus.State == EngineState.LoadFailed)
        {
            announceWhenReady = false;
            app.Announcer.Say(app.EngineStatus.Explanation, interrupt: false);
        }
    }

    public void Detach()
    {
        app.PropertyChanged -= AppChanged;
        download?.Cancel();
    }

    private void Changed(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

internal partial class SetupWindow : Window
{
    private readonly SetupViewModel model;
    private readonly AppServices app;
    private readonly DispatcherTimer poller = new() { Interval = TimeSpan.FromSeconds(2) };

    public SetupWindow(AppServices app, string describeShortcut)
    {
        InitializeComponent();
        this.app = app;
        model = new SetupViewModel(app, describeShortcut);
        DataContext = model;
        poller.Tick += (_, _) => model.Refresh();
        Loaded += (_, _) =>
        {
            model.Refresh();
            poller.Start();
            Intro.Focus();
        };
        Closed += (_, _) =>
        {
            poller.Stop();
            model.Detach();
        };
    }

    private async void DownloadFast_Click(object sender, RoutedEventArgs e) => await model.DownloadAsync(ModelVariant.Qwen3VL2B);

    private async void DownloadGpu_Click(object sender, RoutedEventArgs e) => await model.DownloadAsync(ModelVariant.DirectML);

    private async void DownloadCpu_Click(object sender, RoutedEventArgs e) => await model.DownloadAsync(ModelVariant.Cpu);

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => model.CancelDownload();

    private async void Import_Click(object sender, RoutedEventArgs e) => await model.ImportWhatsAppAsync();

    private async void DownloadStickerMaker_Click(object sender, RoutedEventArgs e) => await model.DownloadStickerMakerAsync();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppContext.BaseDirectory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        app.Settings.HasCompletedSetup = true;
        Close();
    }
}
