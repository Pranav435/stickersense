using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using StickerSense.App.Platform;
using StickerSense.Core;

namespace StickerSense.App.Views;

/// <summary>One row in the picker: a library sticker, a GIPHY result, or "More GIPHY results".</summary>
internal sealed class PickerItem : INotifyPropertyChanged
{
    private StickerRecord? record;
    private StickerReading? giphyReading;
    private ImageSource? thumbnail;

    public PickerItem(StickerRecord record)
    {
        this.record = record;
        Id = $"lib-{record.Id}";
    }

    public PickerItem(GiphyItem gif, StickerReading? reading)
    {
        Gif = gif;
        giphyReading = reading;
        Id = gif.Key;
    }

    private PickerItem()
    {
        Id = "load-more";
        IsLoadMore = true;
    }

    /// <summary>The last row while GIPHY has more pages.</summary>
    public static PickerItem LoadMore() => new();

    public string Id { get; }
    public GiphyItem? Gif { get; }
    public bool IsLoadMore { get; }
    public bool IsLibrary => record is not null;

    public StickerRecord? Record
    {
        get => record;
        set
        {
            record = value;
            Changed(nameof(Label));
        }
    }

    /// <summary>On-device description of a GIPHY result.</summary>
    public StickerReading? GiphyReading
    {
        get => giphyReading;
        set
        {
            giphyReading = value;
            Changed(nameof(Label));
        }
    }

    public ImageSource? Thumbnail
    {
        get => thumbnail;
        set
        {
            thumbnail = value;
            Changed();
        }
    }

    /// <summary>What the screen reader reads for this row.</summary>
    public string Label =>
        record is not null ? record.ScreenReaderLabel
        : Gif is not null ? PickerSearch.Label(Gif, giphyReading)
        : PickerSearch.LoadMoreLabel;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed record Choice<T>(T Value, string Title);

/// <summary>
/// The accessible sticker picker: your own stickers at once, GIPHY results as they arrive (one
/// announcement for both when GIPHY answers within 700 ms), more GIPHY pages on demand, and
/// on-device descriptions of GIPHY results, with the selected one described first.
/// </summary>
internal sealed class PickerViewModel : INotifyPropertyChanged
{
    private const string SendHint = "Press Enter to paste the first one, or Down Arrow to go to the list.";
    private static readonly TimeSpan GiphyGrace = TimeSpan.FromMilliseconds(700);

    private readonly AppServices app;
    private readonly DispatcherTimer debounce;
    /// <summary>GIPHY results waiting for an on-device description; the selected one jumps the queue.</summary>
    private readonly LinkedList<PickerItem> describeQueue = new();
    private CancellationTokenSource? searchRun;
    private CancellationTokenSource? focusDescribeRun;
    private (string Query, int GifOffset, int StickerOffset, bool HasMore)? paging;
    private bool loadingMore;
    private bool describing;
    private bool stopped;
    private int libraryCount;
    private int giphyCount;
    private string query = "";
    private SourceFilter filter = SourceFilter.All;
    private PickerItem? selected;
    private string status = "";

    public PickerViewModel(AppServices app, IntPtr targetWindow)
    {
        this.app = app;
        TargetWindow = targetWindow;
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            _ = SearchAsync();
        };
    }

    private PickerSearch Search => app.PickerSearch;

    /// <summary>The chat window to paste into (zero when the picker was opened without one).</summary>
    public IntPtr TargetWindow { get; }

    /// <summary>
    /// Characters to delete before pasting: 2 when the picker was opened by typing "::". Closing
    /// the picker without choosing leaves them as typed.
    /// </summary>
    public int CharactersToReplace { get; init; }

    /// <summary>Closes the picker window (set by the window).</summary>
    public Action Close { get; set; } = () => { };

    /// <summary>Asks the window to move keyboard focus to a row (after "More GIPHY results").</summary>
    public event Action<PickerItem>? MoveFocusTo;

    public ObservableCollection<PickerItem> Items { get; } = [];

    public IReadOnlyList<Choice<SourceFilter>> Filters { get; } =
        Enum.GetValues<SourceFilter>().Select(f => new Choice<SourceFilter>(f, f.Title())).ToList();

    public string Query
    {
        get => query;
        set
        {
            if (!Set(ref query, value)) return;
            debounce.Stop();
            debounce.Start();
        }
    }

    public SourceFilter Filter
    {
        get => filter;
        set
        {
            if (Set(ref filter, value)) _ = SearchAsync();
        }
    }

    public PickerItem? Selected
    {
        get => selected;
        set
        {
            if (Set(ref selected, value)) SelectionChanged();
        }
    }

    public string Status { get => status; private set => Set(ref status, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool WantsGiphy(string text, SourceFilter scope) => scope is SourceFilter.All or SourceFilter.Giphy && text.Trim().Length > 0;

    // MARK: Search

    public async Task SearchAsync()
    {
        searchRun?.Cancel();
        var run = searchRun = new CancellationTokenSource();
        var ct = run.Token;
        var text = query;
        var scope = filter;
        describeQueue.Clear();
        paging = null;

        // Your own stickers first: they're local and instant.
        var library = await Task.Run(() => Search.LibraryResults(text, scope), ct);
        if (ct.IsCancellationRequested) return;
        Items.Clear();
        foreach (var record in library) Items.Add(new PickerItem(record));
        libraryCount = Items.Count;
        giphyCount = 0;
        if (selected is not null && !Items.Contains(selected)) Selected = null;
        _ = LoadThumbnailsAsync(Items.ToList(), ct);
        if (!WantsGiphy(text, scope))
        {
            Announce("");
            return;
        }

        // Give GIPHY a moment so one announcement can cover both; otherwise speak now and add GIPHY later.
        var giphy = Search.GiphyPageAsync(text, app.Settings.GiphyKey, ct: ct);
        var finished = await Task.WhenAny(giphy, Task.Delay(GiphyGrace));
        if (ct.IsCancellationRequested) return;
        if (finished == giphy)
        {
            var results = await giphy;
            AppendGiphy(results, text);
            Announce(results.Note);
            return;
        }
        Announce("", giphyPending: true);
        PickerSearch.GiphyResults late;
        try
        {
            late = await giphy;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested || query != text) return;
        AppendGiphy(late, text);
        if (late.Items.Count > 0) app.Announcer.Say($"Plus {late.Items.Count} from GIPHY.", interrupt: false);
        else if (late.Note.Length > 0) app.Announcer.Say(late.Note, interrupt: false);
    }

    /// <summary>Adds a page of GIPHY results (and the "More GIPHY results" row while there are more). Returns the first new row.</summary>
    private PickerItem? AppendGiphy(PickerSearch.GiphyResults results, string text)
    {
        if (Items.LastOrDefault() is { IsLoadMore: true } more) Items.Remove(more);
        var added = results.Items.Select(gif => new PickerItem(gif, Search.Reading(gif))).ToList();
        foreach (var item in added) Items.Add(item);
        giphyCount += added.Count;
        paging = (text, results.NextGifOffset, results.NextStickerOffset, results.HasMore);
        if (results.HasMore) Items.Add(PickerItem.LoadMore());
        _ = LoadThumbnailsAsync(added, searchRun?.Token ?? CancellationToken.None);
        foreach (var item in added.Where(i => i.GiphyReading is null)) describeQueue.AddLast(item);
        StartDescribing();
        return added.FirstOrDefault();
    }

    private void Announce(string note, bool giphyPending = false)
    {
        var first = Items.FirstOrDefault(i => !i.IsLoadMore);
        Status = PickerSearch.ResultsSummary(libraryCount, giphyCount, query, first?.Label, note, SendHint, giphyPending);
        app.Announcer.Say(Status, interrupt: false);
    }

    /// <summary>The next page of GIPHY results (50 more).</summary>
    public async Task LoadMoreAsync(bool announce)
    {
        if (paging is not { HasMore: true } page || loadingMore || page.Query != query) return;
        loadingMore = true;
        try
        {
            var results = await Search.GiphyPageAsync(page.Query, app.Settings.GiphyKey, page.GifOffset, page.StickerOffset);
            if (stopped || query != page.Query) return;
            var firstNew = AppendGiphy(results, page.Query);
            if (!announce) return;
            if (results.Items.Count == 0)
            {
                app.Announcer.Say(results.Note.Length > 0 ? results.Note : "No more from GIPHY.");
                return;
            }
            app.Announcer.Say($"{results.Items.Count} more from GIPHY.");
            if (firstNew is not null)
            {
                Selected = firstNew;
                MoveFocusTo?.Invoke(firstNew);
            }
        }
        finally
        {
            loadingMore = false;
        }
    }

    private async Task LoadThumbnailsAsync(List<PickerItem> items, CancellationToken ct)
    {
        foreach (var item in items.Take(120))
        {
            if (ct.IsCancellationRequested || stopped) return;
            if (item.Record is { } record) item.Thumbnail = await Thumbnails.FromFileAsync(app.Library.ImagePath(record));
            else if (item.Gif is { } gif) item.Thumbnail = await Thumbnails.FromFileAsync(await Search.SmallFileAsync(gif));
        }
    }

    // MARK: Describing GIPHY results and undescribed stickers

    private void StartDescribing()
    {
        if (describing || stopped) return;
        describing = true;
        _ = DescribeQueueAsync();
    }

    private async Task DescribeQueueAsync()
    {
        try
        {
            while (!stopped && describeQueue.First is { } node)
            {
                describeQueue.RemoveFirst();
                var item = node.Value;
                if (item.GiphyReading is not null || !Items.Contains(item)) continue;
                var reading = await Search.DescribeAsync(item.Gif!);
                if (reading is null || stopped) continue;
                item.GiphyReading = reading;
                // The user is on this one and was waiting for it: say it.
                if (ReferenceEquals(selected, item)) app.Announcer.Say(SpokenText.ListLabel(reading), interrupt: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            describing = false;
        }
    }

    /// <summary>Arrowing onto something: describe it first if it isn't yet, and fetch more near the end.</summary>
    private void SelectionChanged()
    {
        focusDescribeRun?.Cancel();
        if (selected is not { } item) return;
        if (Items.IndexOf(item) >= Items.Count - 6) _ = LoadMoreAsync(announce: false);
        if (item.Record is { IsDescribed: false } record)
        {
            var run = focusDescribeRun = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    var updated = await app.Pipeline.DescribeStoredAsync(record, run.Token);
                    await Ui.RunAsync(() =>
                    {
                        item.Record = updated;
                        if (!run.IsCancellationRequested && ReferenceEquals(selected, item)) app.Announcer.Say(updated.ScreenReaderLabel, interrupt: false);
                    });
                }
                catch (Exception e) when (e is OperationCanceledException or DescribeException)
                {
                }
            });
        }
        else if (item.Gif is not null && item.GiphyReading is null)
        {
            describeQueue.Remove(item);
            describeQueue.AddFirst(item);
            StartDescribing();
        }
    }

    /// <summary>The window closed: stop describing and loading for it.</summary>
    public void Stop()
    {
        stopped = true;
        debounce.Stop();
        searchRun?.Cancel();
        focusDescribeRun?.Cancel();
        describeQueue.Clear();
    }

    // MARK: Actions

    public async Task SendFirstAsync()
    {
        if (Items.FirstOrDefault(i => !i.IsLoadMore) is not { } first)
        {
            app.Earcons.Play(Earcon.Failed);
            app.Announcer.Say("Nothing to paste.");
            return;
        }
        await SendAsync(first);
    }

    public async Task SendAsync(PickerItem item)
    {
        if (item.IsLoadMore)
        {
            await LoadMoreAsync(announce: true);
            return;
        }
        var file = await FilePathAsync(item);
        if (file is null)
        {
            app.Earcons.Play(Earcon.Failed);
            app.Announcer.Say("Couldn't get that sticker.");
            return;
        }
        Close();
        try
        {
            var outcome = await Paster.PasteAsync(file, TargetWindow, CharactersToReplace);
            app.Log.Add($"Picker: {outcome.Message} (replaced {CharactersToReplace} typed character(s))");
            app.Earcons.Play(Earcon.Tick);
            app.Announcer.Say(outcome.Message);
        }
        catch (Exception e) when (e is ImagePrepException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            app.Log.Add($"Picker: paste failed: {e.Message}");
            app.Earcons.Play(Earcon.Failed);
            app.Announcer.Say("Couldn't paste that sticker.");
            return;
        }
        await RememberAsync(item);
    }

    public async Task CopyAsync(PickerItem item)
    {
        if (item.IsLoadMore) return;
        var file = await FilePathAsync(item);
        if (file is null) return;
        await Paster.CopyAsync(file);
        app.Earcons.Play(Earcon.Tick);
        app.Announcer.Say("Copied.");
        await RememberAsync(item);
    }

    public async Task DescribeInDetailAsync(PickerItem item)
    {
        if (item.IsLoadMore) return;
        app.Earcons.Play(Earcon.Working);
        try
        {
            string text;
            if (item.Record is { } record)
            {
                var described = await app.Pipeline.DescribeStoredAsync(record);
                item.Record = described;
                text = await app.Pipeline.DetailAsync(described, null, DescriptionContext.File);
            }
            else
            {
                text = await Search.DetailAsync(item.Gif!, app.Pipeline.Describer);
            }
            app.Earcons.Play(Earcon.Ready);
            app.Announcer.Say(text);
        }
        catch (Exception e) when (e is DescribeException or ImagePrepException or OperationCanceledException)
        {
            app.Earcons.Play(Earcon.Failed);
            app.Announcer.Say(SpokenText.Failure(e));
        }
    }

    public void Remove(PickerItem item)
    {
        if (item.Record is not { } record)
        {
            if (!item.IsLoadMore) app.Announcer.Say("Only stickers in your library can be removed.");
            return;
        }
        app.Library.Delete(record.Id);
        Items.Remove(item);
        libraryCount--;
        app.Announcer.Say("Removed.");
    }

    private async Task RememberAsync(PickerItem item)
    {
        if (item.Record is { } record) app.Library.MarkSent(record.Id);
        else if (item.Gif is { } gif) await Search.MarkSentAsync(gif);
    }

    private async Task<string?> FilePathAsync(PickerItem item) =>
        item.Record is { } record ? app.Library.ImagePath(record) is { } path && File.Exists(path) ? path : null
        : item.Gif is { } gif ? await Search.OriginalFileAsync(gif)
        : null;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
