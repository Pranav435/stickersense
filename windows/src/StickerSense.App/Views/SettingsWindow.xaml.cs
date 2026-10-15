using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using StickerSense.App.Engine;
using StickerSense.App.Platform;
using StickerSense.App.Speech;
using StickerSense.Core;

namespace StickerSense.App.Views;

internal sealed class SettingsViewModel : INotifyPropertyChanged
{
    public SettingsViewModel(AppServices app)
    {
        App = app;
        app.PropertyChanged += AppChanged;
        app.Settings.PropertyChanged += SettingsChanged;
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsStore.StickerModel)) StickerModelChanged();
    }

    private void StickerModelChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StickerModelSummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDownloadStickerModel)));
    }

    private void AppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppServices.StickerDownloadPercent)) StickerModelChanged();
        if (e.PropertyName is nameof(AppServices.LibraryCount) or nameof(AppServices.UndescribedCount))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LibrarySummary)));
        if (e.PropertyName is nameof(AppServices.EngineStatus))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EngineSummary)));
    }

    public void Detach()
    {
        App.PropertyChanged -= AppChanged;
        App.Settings.PropertyChanged -= SettingsChanged;
    }

    public IReadOnlyList<Choice<StickerModel>> StickerModels { get; } =
        Enum.GetValues<StickerModel>().Select(m => new Choice<StickerModel>(m, StickerMakerCatalog.Title(m))).ToList();

    public string StickerModelSummary
    {
        get
        {
            var model = Settings.StickerModel;
            var state = App.StickerDownloadPercent >= 0 ? $"Downloading: {App.StickerDownloadPercent} percent."
                : App.IsStickerModelInstalled(model) ? "Downloaded."
                : $"Not downloaded yet ({StickerMakerCatalog.SpokenSize(model)}). It downloads the first time you make a sticker.";
            return $"{state} {StickerMakerCatalog.Credit(model)}";
        }
    }

    public bool CanDownloadStickerModel => App.StickerDownloadPercent < 0 && !App.IsStickerModelInstalled(Settings.StickerModel);

    public AppServices App { get; }
    public SettingsStore Settings => App.Settings;
    public IReadOnlyList<string> Keys => KeyCombo.Keys;
    public bool WhatsAppInstalled => WhatsAppImporter.IsInstalled;

    public IReadOnlyList<Choice<AutoScope>> AutoScopes { get; } =
        Enum.GetValues<AutoScope>().Select(s => new Choice<AutoScope>(s, s.Title())).ToList();

    public IReadOnlyList<Choice<EngineDevice>> Devices { get; } =
    [
        new(EngineDevice.Automatic, "The graphics card if it has its own memory, otherwise the processor (recommended)"),
        new(EngineDevice.GraphicsCard, "The graphics card"),
        new(EngineDevice.ProcessorOnly, "The processor only"),
    ];

    /// <summary>The installed models to choose between (with "automatic": the fast one when installed).</summary>
    public IReadOnlyList<Choice<ModelVariant?>> Models =>
        new[] { new Choice<ModelVariant?>(null, "Automatic: the fast model when it's installed (recommended)") }
            .Concat(App.InstalledModels().Select(v => new Choice<ModelVariant?>(v, v == ModelVariant.Qwen3VL2B
                ? "Qwen3-VL 2B (fast)"
                : "Phi-3.5-vision")))
            .ToList();

    public IReadOnlyList<Choice<SpeechOutput>> SpeechOutputs { get; } =
    [
        new(SpeechOutput.ScreenReader, "My screen reader (recommended)"),
        new(SpeechOutput.WindowsVoice, "The Windows voice"),
    ];

    public string LibrarySummary =>
        $"{App.LibraryCount} stickers in your library{(App.UndescribedCount > 0 ? $", {App.UndescribedCount} still being described" : "")}.";

    public string EngineSummary => App.EngineStatus.IsReady && App.Describer.RunningOn is { } device
        ? $"{(App.Describer.LoadedDirectory is { } directory ? ModelCatalog.FamilyName(ModelCatalog.DetectVariant(directory)) : "The model")} is running on {device}."
        : App.EngineStatus.Explanation;

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal partial class SettingsWindow : Window
{
    private readonly AppServices app;
    private readonly Action openSetup;
    private string savedKey;

    public SettingsWindow(AppServices app, Action openSetup)
    {
        InitializeComponent();
        this.app = app;
        this.openSetup = openSetup;
        var model = new SettingsViewModel(app);
        DataContext = model;
        savedKey = app.Settings.GiphyKey;
        GiphyBox.Password = savedKey;
        Loaded += (_, _) => FirstControl.Focus();
        Closing += (_, _) => SaveGiphyKey(announce: false);
        Closed += (_, _) => model.Detach();
    }

    private void SaveGiphyKey(bool announce)
    {
        var key = GiphyBox.Password.Trim();
        if (key == savedKey) return;
        app.Settings.GiphyKey = key;
        savedKey = key;
        if (announce) app.Announcer.Say(key.Length == 0 ? "GIPHY key removed." : "GIPHY key saved.", interrupt: false);
    }

    private void GiphyBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => SaveGiphyKey(announce: true);

    private async void Import_Click(object sender, RoutedEventArgs e) => await app.ImportWhatsAppAsync();

    private async void DownloadStickerMaker_Click(object sender, RoutedEventArgs e) => await app.DownloadStickerModelAsync(app.Settings.StickerModel);

    private void GiphyLink_Click(object sender, RoutedEventArgs e) => OpenUrl("https://developers.giphy.com/dashboard/");

    private void Setup_Click(object sender, RoutedEventArgs e) => openSetup();

    private void Log_Click(object sender, RoutedEventArgs e) => OpenUrl(app.Log.FilePath);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
        }
    }
}
