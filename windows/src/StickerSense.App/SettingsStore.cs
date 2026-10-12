using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using StickerSense.App.Engine;
using StickerSense.App.Platform;
using StickerSense.App.Speech;
using StickerSense.Core;

namespace StickerSense.App;

/// <summary>User preferences, saved as <c>settings.json</c> next to the library. The GIPHY key is kept separately with DPAPI.</summary>
internal sealed class SettingsStore : INotifyPropertyChanged, IDescribeSettings
{
    private readonly string path;
    private readonly SecretStore secrets;
    private Values values;

    /// <summary>The persisted values (field names are the JSON keys).</summary>
    private sealed record Values
    {
        public bool AutoDescribe { get; init; } = true;
        public AutoScope AutoScope { get; init; } = AutoScope.ChatApps;
        public bool Earcons { get; init; } = true;
        public double EarconVolume { get; init; } = 0.35;
        public bool IncludeMeaning { get; init; }
        public SpeechOutput SpeechOutput { get; init; } = SpeechOutput.ScreenReader;
        public EngineDevice EngineDevice { get; init; } = EngineDevice.Automatic;
        public string DescribeKey { get; init; } = "D";
        public string PickerKey { get; init; } = "S";
        public string HistoryKey { get; init; } = "H";
        public string AutoToggleKey { get; init; } = "A";
        public bool HasCompletedSetup { get; init; }
        public bool ColonTrigger { get; init; } = true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public SettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "settings.json");
        secrets = new SecretStore(directory);
        values = Load(path);
    }

    /// <summary>Raised after any change; the argument says whether a shortcut changed.</summary>
    public event Action<bool>? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool AutoDescribe { get => values.AutoDescribe; set => Update(values with { AutoDescribe = value }); }
    public AutoScope AutoScope { get => values.AutoScope; set => Update(values with { AutoScope = value }); }
    public bool Earcons { get => values.Earcons; set => Update(values with { Earcons = value }); }
    public double EarconVolume
    {
        get => values.EarconVolume;
        set
        {
            Update(values with { EarconVolume = Math.Clamp(value, 0.05, 1) });
            OnPropertyChanged(nameof(VolumePercent));
        }
    }

    /// <summary>The earcon volume as 5–100, for the Settings slider.</summary>
    public int VolumePercent { get => (int)Math.Round(EarconVolume * 100); set => EarconVolume = value / 100.0; }
    public bool IncludeMeaning { get => values.IncludeMeaning; set => Update(values with { IncludeMeaning = value }); }
    public SpeechOutput SpeechOutput { get => values.SpeechOutput; set => Update(values with { SpeechOutput = value }); }
    public EngineDevice EngineDevice { get => values.EngineDevice; set => Update(values with { EngineDevice = value }); }
    public string DescribeKey { get => values.DescribeKey; set => Update(values with { DescribeKey = value }, hotkeys: true); }
    public string PickerKey { get => values.PickerKey; set => Update(values with { PickerKey = value }, hotkeys: true); }
    public string HistoryKey { get => values.HistoryKey; set => Update(values with { HistoryKey = value }, hotkeys: true); }
    public string AutoToggleKey { get => values.AutoToggleKey; set => Update(values with { AutoToggleKey = value }, hotkeys: true); }
    public bool HasCompletedSetup { get => values.HasCompletedSetup; set => Update(values with { HasCompletedSetup = value }, notify: false); }

    /// <summary>Typing "::" in a chat opens the sticker picker.</summary>
    public bool ColonTrigger { get => values.ColonTrigger; set => Update(values with { ColonTrigger = value }); }

    public KeyCombo DescribeCombo => KeyCombo.For(DescribeKey, "D");
    public KeyCombo PickerCombo => KeyCombo.For(PickerKey, "S");
    public KeyCombo HistoryCombo => KeyCombo.For(HistoryKey, "H");
    public KeyCombo AutoToggleCombo => KeyCombo.For(AutoToggleKey, "A");

    public string GiphyKey
    {
        get => secrets.ReadGiphyKey();
        set
        {
            secrets.WriteGiphyKey(value);
            OnPropertyChanged();
        }
    }

    public bool LaunchAtLogin
    {
        get => StartupRegistration.IsEnabled;
        set
        {
            StartupRegistration.IsEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Meaning is only generated when it will be spoken: every extra line the model writes costs time.</summary>
    public DescriberOptions DescriberOptions => new() { Language = null, IncludeMeaning = IncludeMeaning };

    private void Update(Values next, bool hotkeys = false, bool notify = true, [CallerMemberName] string? name = null)
    {
        if (next == values) return;
        values = next;
        Save();
        OnPropertyChanged(name);
        if (notify) Changed?.Invoke(hotkeys);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Values Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<Values>(File.ReadAllText(path), JsonOptions) ?? new Values();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return new Values();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(values, JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
