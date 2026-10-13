using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using StickerSense.Core;

namespace StickerSense.App.Diagnostics;

/// <summary>
/// <c>StickerSense.exe --download-model qwen|directml|cpu [--to folder]</c>: downloads the model the
/// same way Setup does, printing progress and the size and time taken.
/// </summary>
internal static class DownloadModelCommand
{
    /// <summary>Reports progress on the downloading thread (Progress&lt;T&gt; would need a UI thread).</summary>
    private sealed class Reporter(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    public static int Run(string[] args)
    {
        var output = ConsoleOutput.Open();
        var i = Array.IndexOf(args, "--download-model");
        var name = i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : "";
        var variant = name switch
        {
            "cpu" => ModelVariant.Cpu,
            "directml" => ModelVariant.DirectML,
            _ => ModelVariant.Qwen3VL2B,
        };
        var to = Array.IndexOf(args, "--to") is var t and >= 0 && t + 1 < args.Length
            ? args[t + 1]
            : ModelCatalog.UserModelDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), variant);
        output.WriteLine($"Downloading the {variant} model ({ModelCatalog.HuggingFaceFolder(variant)}) to {to}");
        var milestones = new PercentMilestones(5);
        var watch = Stopwatch.StartNew();
        long first = -1, total = 0;
        var progress = new Reporter(p =>
        {
            if (first < 0) first = p.BytesDone;
            total = p.BytesTotal;
            if (milestones.Next(p.Percent) is { } m)
            {
                var rate = (p.BytesDone - first) / 1e6 / Math.Max(0.001, watch.Elapsed.TotalSeconds);
                output.WriteLine($"{m,3}%  {p.BytesDone / 1e6,7:0} of {p.BytesTotal / 1e6:0} MB  {rate:0.0} MB/s  {p.CurrentFile}");
            }
        });
        try
        {
            new HuggingFaceDownloader().DownloadAsync(variant, to, progress).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is ModelDownloadException or IOException or System.Net.Http.HttpRequestException)
        {
            output.WriteLine($"FAILED: {e.Message}");
            return 1;
        }
        var seconds = watch.Elapsed.TotalSeconds;
        var downloaded = total - Math.Max(0, first);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            variant = variant.ToString(),
            folder = to,
            totalBytes = total,
            downloadedBytes = downloaded,
            seconds = Math.Round(seconds, 1),
            megabytesPerSecond = Math.Round(downloaded / 1e6 / Math.Max(seconds, 0.001), 1),
            complete = ModelCatalog.IsComplete(to),
        }));
        return 0;
    }
}

/// <summary>
/// <c>StickerSense.exe --test-window</c>: a tiny stand-in for a chat, for end-to-end tests: a
/// sticker that screen readers see as an image named "Sticker from Test User, 10:00", and a
/// message box. (Run the main app with <c>--watch-app stickersense</c> so auto mode and "::" treat it as a chat.)
/// </summary>
internal static class TestWindowCommand
{
    public static int Run(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new TestChatWindow();
        app.Run(window);
        return 0;
    }
}

internal sealed class TestChatWindow : Window
{
    public TestChatWindow()
    {
        Title = "StickerSense test chat";
        Width = 420;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Test User", FontWeight = FontWeights.SemiBold });
        using (var sticker = TestStickers.Birthday())
        {
            panel.Children.Add(new TestSticker(ToBitmap(ImagePrep.PngData(sticker)), "Sticker from Test User, 10:00"));
        }
        var box = new TextBox { Margin = new Thickness(0, 16, 0, 0), MinHeight = 28 };
        AutomationProperties.SetName(box, "Type a message");
        panel.Children.Add(box);
        Content = panel;
    }

    private static BitmapImage ToBitmap(byte[] png)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A focusable picture exposed to UI Automation as an image, like a sticker in a chat.</summary>
    private sealed class TestSticker : ContentControl
    {
        public TestSticker(BitmapSource image, string name)
        {
            Focusable = true;
            IsTabStop = true;
            Width = 180;
            Height = 180;
            Margin = new Thickness(0, 8, 0, 0);
            HorizontalAlignment = HorizontalAlignment.Left;
            Content = new Image { Source = image, Stretch = System.Windows.Media.Stretch.Uniform };
            AutomationProperties.SetName(this, name);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(TestSticker owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
            protected override string GetClassNameCore() => "TestSticker";
            protected override bool IsControlElementCore() => true;
            protected override List<AutomationPeer>? GetChildrenCore() => null;
        }
    }
}
