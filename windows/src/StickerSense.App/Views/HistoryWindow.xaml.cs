using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StickerSense.Core;

namespace StickerSense.App.Views;

/// <summary>One row of the history. Updated in place so the focused row survives refreshes.</summary>
internal sealed class HistoryRow(StickerRecord record, string label) : INotifyPropertyChanged
{
    private string label = label;

    public StickerRecord Record { get; set; } = record;

    public string Label
    {
        get => label;
        set
        {
            if (label == value) return;
            label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Everything described recently, newest first. Enter speaks the detailed description.</summary>
internal sealed class HistoryViewModel : INotifyPropertyChanged
{
    private readonly AppServices app;
    private HistoryRow? selected;

    public HistoryViewModel(AppServices app)
    {
        this.app = app;
    }

    public ObservableCollection<HistoryRow> Records { get; } = [];

    public HistoryRow? Selected
    {
        get => selected;
        set
        {
            selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Refreshes the rows. When the same stickers are listed, labels change in place; otherwise
    /// (only if <paramref name="allowRebuild"/>) the list is rebuilt.
    /// </summary>
    /// <returns>True when the list was rebuilt (focus needs restoring).</returns>
    public bool Reload(bool allowRebuild = true)
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = app.Library.History();
        if (fresh.Select(r => r.Id).SequenceEqual(Records.Select(r => r.Record.Id)))
        {
            for (var i = 0; i < fresh.Count; i++)
            {
                Records[i].Record = fresh[i];
                Records[i].Label = Label(fresh[i], now);
            }
            return false;
        }
        if (!allowRebuild) return false;
        var selectedId = selected?.Record.Id;
        Records.Clear();
        foreach (var record in fresh) Records.Add(new HistoryRow(record, Label(record, now)));
        Selected = Records.FirstOrDefault(r => r.Record.Id == selectedId);
        return true;
    }

    private string Label(StickerRecord record, DateTimeOffset now)
    {
        var text = record.Reading is { } reading ? SpokenText.Short(reading, app.Settings.IncludeMeaning) : "Not described.";
        if (!string.IsNullOrEmpty(record.Detailed)) text += " Detail available.";
        return $"{text} {record.Source.DisplayName}, {SpokenText.RelativeTime(record.LastSeenAt, now)}.";
    }

    public void SpeakShort(HistoryRow row)
    {
        if (row.Record.Reading is { } reading) app.Announcer.Say(SpokenText.Short(reading, includeVibe: true));
    }

    public async Task SpeakDetailAsync(HistoryRow row)
    {
        var record = app.Library.Record(row.Record.Id) ?? row.Record;
        if (!string.IsNullOrEmpty(record.Detailed))
        {
            app.Announcer.Say(record.Detailed);
            return;
        }
        if (record.ImageFile is null)
        {
            app.Announcer.Say("No detail saved for this one. Photos aren't kept, for privacy.");
            return;
        }
        app.Earcons.Play(Earcon.Working);
        try
        {
            var text = await app.Pipeline.DetailAsync(record, null, DescriptionContext.File);
            app.Earcons.Play(Earcon.Ready);
            app.Announcer.Say(text);
            Reload(allowRebuild: false);
        }
        catch (Exception e) when (e is DescribeException or OperationCanceledException)
        {
            app.Earcons.Play(Earcon.Failed);
            app.Announcer.Say(SpokenText.Failure(e));
        }
    }

    public void CopyText(HistoryRow row)
    {
        var record = app.Library.Record(row.Record.Id) ?? row.Record;
        var text = record.Detailed ?? (record.Reading is { } reading ? SpokenText.Short(reading, includeVibe: true) : "");
        try
        {
            Clipboard.SetText(text);
            app.Announcer.Say("Description copied.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            app.Announcer.Say("Couldn't copy. Try again.");
        }
    }

    public void Remove(HistoryRow row)
    {
        app.Library.Delete(row.Record.Id);
        Records.Remove(row);
        app.Announcer.Say("Removed.");
    }

    public void ClearAll()
    {
        foreach (var row in Records.ToList()) app.Library.Delete(row.Record.Id);
        Records.Clear();
        app.Announcer.Say("History cleared.");
    }
}

internal partial class HistoryWindow : Window
{
    private readonly HistoryViewModel model;
    private readonly AppServices app;

    public HistoryWindow(AppServices app, string describeShortcut)
    {
        InitializeComponent();
        this.app = app;
        model = new HistoryViewModel(app);
        DataContext = model;
        EmptyText.Text = $"Nothing yet. Put focus on a sticker and press {describeShortcut}, and it will show up here.";
        Loaded += (_, _) =>
        {
            Refresh();
            if (model.Records.Count == 0)
            {
                app.Announcer.Say(EmptyText.Text, interrupt: false);
                ClearButton.Focus();
            }
            else
            {
                FocusRow(model.Records[0]);
            }
        };
        app.Library.Changed += LibraryChanged;
        Closed += (_, _) => app.Library.Changed -= LibraryChanged;
        // New stickers may have been described in a chat meanwhile.
        Activated += (_, _) =>
        {
            if (!model.Reload()) return;
            UpdateEmptyState();
            if (model.Records.Count > 0) FocusRow(model.Selected ?? model.Records[0]);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    /// <summary>While the user is in the list, only update labels in place; never pull the focused row away.</summary>
    private void LibraryChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (IsActive) model.Reload(allowRebuild: false);
        else Refresh();
    });

    private void Refresh()
    {
        model.Reload();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var empty = model.Records.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = !empty;
    }

    private void FocusRow(HistoryRow row)
    {
        model.Selected = row;
        Records.ScrollIntoView(row);
        Records.UpdateLayout();
        (Records.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
    }

    private async void Records_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (model.Selected is not { } row) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await model.SpeakDetailAsync(row);
                break;
            case Key.Space:
                e.Handled = true;
                model.SpeakShort(row);
                break;
            case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                e.Handled = true;
                model.CopyText(row);
                break;
            case Key.Delete:
                e.Handled = true;
                var index = Records.SelectedIndex;
                model.Remove(row);
                if (model.Records.Count > 0) FocusRow(model.Records[Math.Min(index, model.Records.Count - 1)]);
                else Refresh();
                break;
        }
    }

    private async void Records_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (model.Selected is { } row) await model.SpeakDetailAsync(row);
    }

    private static HistoryRow? Target(object sender) => (sender as MenuItem)?.DataContext as HistoryRow;

    private async void DetailMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Target(sender) is { } row) await model.SpeakDetailAsync(row);
    }

    private void ShortMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Target(sender) is { } row) model.SpeakShort(row);
    }

    private void CopyMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Target(sender) is { } row) model.CopyText(row);
    }

    private void RemoveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Target(sender) is { } row) model.Remove(row);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "This also removes these stickers from the picker.", "Clear all sticker history?",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        model.ClearAll();
        Refresh();
    }
}
