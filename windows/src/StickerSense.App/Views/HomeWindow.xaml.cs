using System.ComponentModel;
using System.Windows;
using StickerSense.App.Platform;

namespace StickerSense.App.Views;

/// <summary>One shortcut, as shown and as spoken.</summary>
internal sealed record ShortcutRow(string Keys, string What, string Spoken);

/// <summary>StickerSense's main window: what it's doing right now, and a way into everything.</summary>
internal sealed class HomeViewModel : INotifyPropertyChanged
{
    private readonly AppServices app;

    public HomeViewModel(AppServices app)
    {
        this.app = app;
        app.PropertyChanged += AppChanged;
        app.Settings.PropertyChanged += SettingsChanged;
    }

    public SettingsStore Settings => app.Settings;

    public string ModelText => app.EngineStatus.IsReady
        ? app.Describer.RunningOn is { } device ? $"Ready, on {device}" : "Ready"
        : app.EngineStatus.Explanation;

    public string LibraryText
    {
        get
        {
            var text = $"{app.LibraryCount} sticker{(app.LibraryCount == 1 ? "" : "s")}";
            if (app.UndescribedCount > 0) text += $", {app.UndescribedCount} waiting to be described";
            return text;
        }
    }

    public IReadOnlyList<ShortcutRow> Shortcuts
    {
        get
        {
            ShortcutRow Row(KeyCombo combo, string what) => new(combo.MenuName, what, $"{combo.SpokenName}: {what}");
            var rows = new List<ShortcutRow>
            {
                Row(Settings.DescribeCombo, "Describe what has focus. Press again within 3 seconds for detail."),
                Row(Settings.PickerCombo, "Sticker picker"),
            };
            if (Settings.ColonTrigger) rows.Add(new ShortcutRow("::", "Two colons (::) in a chat: sticker picker", "Two colons in a chat: sticker picker"));
            rows.Add(Row(Settings.HistoryCombo, "Sticker history"));
            rows.Add(Row(Settings.AutoToggleCombo, "Automatic descriptions on or off"));
            return rows;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void AppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppServices.EngineStatus)) Changed(nameof(ModelText));
        if (e.PropertyName is nameof(AppServices.LibraryCount) or nameof(AppServices.UndescribedCount)) Changed(nameof(LibraryText));
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsStore.DescribeKey) or nameof(SettingsStore.PickerKey) or nameof(SettingsStore.HistoryKey)
            or nameof(SettingsStore.AutoToggleKey) or nameof(SettingsStore.ColonTrigger))
        {
            Changed(nameof(Shortcuts));
        }
    }

    public void Detach()
    {
        app.PropertyChanged -= AppChanged;
        app.Settings.PropertyChanged -= SettingsChanged;
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal partial class HomeWindow : Window
{
    /// <summary>Where the main window's buttons lead.</summary>
    public sealed record Actions(Action Picker, Action History, Action Settings, Action Setup, Action Help);

    private readonly Actions actions;

    public HomeWindow(AppServices app, Actions actions)
    {
        InitializeComponent();
        this.actions = actions;
        var model = new HomeViewModel(app);
        DataContext = model;
        Loaded += (_, _) =>
        {
            app.RefreshCounts();
            ModelStatus.Focus();
        };
        Closed += (_, _) => model.Detach();
    }

    private void Picker_Click(object sender, RoutedEventArgs e) => actions.Picker();
    private void History_Click(object sender, RoutedEventArgs e) => actions.History();
    private void Settings_Click(object sender, RoutedEventArgs e) => actions.Settings();
    private void Setup_Click(object sender, RoutedEventArgs e) => actions.Setup();
    private void Help_Click(object sender, RoutedEventArgs e) => actions.Help();
}
