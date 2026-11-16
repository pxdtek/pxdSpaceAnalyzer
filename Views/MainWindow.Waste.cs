using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using pxdSpaceAnalyzer.Core;

namespace pxdSpaceAnalyzer.Views;

public sealed partial class MainWindow
{
    private readonly StackPanel _wasteList = new() { Spacing = 10 };
    private readonly TextBlock _wasteSummary = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _wasteClean = new() { Content = "Clean up selected…", Classes = { "primary" }, IsEnabled = false };
    private readonly HashSet<WasteItem> _picked = [];
    private List<WasteItem> _waste = [];
    private bool _wasteLoaded;
    private bool _wasteBusy;
    private WasteFinder.StoreReport? _store;
    private bool _storeBusy;

    public IReadOnlyList<WasteItem> Waste => _waste;
    public IReadOnlyCollection<WasteItem> PickedWaste => _picked;
    public bool WasteBusy => _wasteBusy;

    private Control BuildWastePage()
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(_wasteSummary);
        var bs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [Grid.ColumnProperty] = 1 };
        var again = new Button { Content = "Look again" };
        again.Click += async (_, _) =>
        {
            if (!_wasteBusy) await FindWasteAsync();
        };
        _wasteClean.Click += async (_, _) => await CleanPickedAsync();
        ToolTip.SetTip(_wasteClean, "Shows what goes and how, and asks first.");
        bs.Children.Add(again);
        bs.Children.Add(_wasteClean);
        top.Children.Add(bs);
        g.Children.Add(top);
        var scroll = new ScrollViewer { Content = _wasteList, Padding = new Thickness(0, 0, 14, 0), [Grid.RowProperty] = 1 };
        g.Children.Add(scroll);
        return g;
    }

    public async Task FindWasteAsync()
    {
        _wasteBusy = true;
        _wasteSummary.Text = "Looking for the usual waste…";
        _wasteList.Children.Clear();
        _progress.IsVisible = true;
        try
        {
            _waste = await Task.Run(() => WasteFinder.Find(_places));
        }
        finally
        {
            _wasteBusy = false;
            _progress.IsVisible = _busy;
        }

        _wasteLoaded = true;
        _picked.Clear();
        ShowWaste();
    }

    /// <summary>Shows a ready-made list (screenshots).</summary>
    public void SetWaste(List<WasteItem> items, WasteFinder.StoreReport? store = null)
    {
        _waste = items;
        _wasteLoaded = true;
        _store = store;
        _picked.Clear();
        ShowWaste();
    }

    public void RefreshWaste() => ShowWaste();

    private void ShowWaste()
    {
        _wasteList.Children.Clear();
        var sorted = _waste.OrderByDescending(i => i.CanClean).ThenByDescending(i => i.Size).ToList();
        var cleanable = _waste.Where(i => i.CanClean).Sum(i => i.Size);
        var reported = _waste.Where(i => !i.CanClean).Sum(i => Math.Max(0, i.Size));
        _wasteSummary.Text = $"{Text.Size(cleanable)} can be cleaned up safely  ·  {Text.Size(reported)} more to know about";
        foreach (var item in sorted) _wasteList.Children.Add(WasteCard(item));
        UpdateCleanButton();
    }

    private void UpdateCleanButton()
    {
        var size = _picked.Sum(i => i.Size);
        _wasteClean.IsEnabled = _picked.Count > 0 && !_wasteBusy;
        _wasteClean.Content = _picked.Count > 0 ? $"Clean up {Text.Size(size)}…" : "Clean up selected…";
    }

    public void PickWaste(WasteItem item, bool on)
    {
        if (on && item.CanClean) _picked.Add(item);
        else _picked.Remove(item);
        UpdateCleanButton();
    }

    private Control WasteCard(WasteItem item)
    {
        var card = new Border { Classes = { "card" }, Padding = new Thickness(16, 12) };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var check = new CheckBox { IsEnabled = item.CanClean, IsChecked = _picked.Contains(item), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 8, 0) };
        check.IsCheckedChanged += (_, _) => PickWaste(item, check.IsChecked == true);
        ToolTip.SetTip(check, item.CanClean ? $"Pick it for clean-up: {item.HowText}." : item.Blocked ?? "Reported only: nothing to clean here.");
        g.Children.Add(check);

        var mid = new StackPanel { Spacing = 4, [Grid.ColumnProperty] = 1 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = item.Display, FontSize = 14.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        if (item.Blocked is { } why) title.Children.Add(Pill(why, "warn"));
        else if (item.How == CleanHow.None) title.Children.Add(Pill("Report only", ""));
        else if (item.Size > 0) title.Children.Add(Pill(item.How == CleanHow.RecycleBin ? "To the Recycle Bin" : "Safe to clean", "good"));
        if (item.OlderThanDays > 0) title.Children.Add(Pill($"Older than {Text.Plural(item.OlderThanDays, "day")}", "info"));
        mid.Children.Add(title);
        mid.Children.Add(new TextBlock { Text = item.Note, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        var where = item.Targets.Count switch
        {
            0 => "",
            1 => item.Targets[0].Path + (item.Targets[0].Pattern is { } pat ? "\\" + pat : ""),
            _ => $"{item.Targets[0].Path}  (+{item.Targets.Count - 1} more)",
        };
        if (where.Length > 0) mid.Children.Add(new TextBlock { Text = where, FontFamily = Mono, FontSize = 11, Foreground = Brush("#6E7891"), TextTrimming = TextTrimming.CharacterEllipsis });

        if (item.Id == "winsxs")
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            if (_store is { } s)
            {
                row.Children.Add(new TextBlock
                {
                    Text = $"DISM: {Text.Size(s.ActualSize)} really used, {Text.Size(Math.Max(0, s.SharedWithWindows))} shared with Windows, {Text.Size(Math.Max(0, s.Backups))} backups."
                           + (s.CleanupRecommended ? " Clean-up recommended: Disk Cleanup > Clean up system files > Windows Update Cleanup." : " No clean-up needed."),
                    Foreground = s.CleanupRecommended ? Warn : Good, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                });
            }
            else if (Elevation.IsElevated && !_places.IsTest)
            {
                var analyze = new Button { Content = _storeBusy ? "Asking DISM… (a minute or two)" : "Analyze", IsEnabled = !_storeBusy, MinHeight = 30, Padding = new Thickness(12, 2) };
                analyze.Click += async (_, _) =>
                {
                    _storeBusy = true;
                    ShowWaste();
                    _store = await WasteFinder.AnalyzeComponentStoreAsync();
                    _storeBusy = false;
                    if (_store is null) ShowNotice("DISM couldn't analyze the component store.");
                    ShowWaste();
                };
                row.Children.Add(analyze);
            }
            else
            {
                row.Children.Add(new TextBlock { Text = "Run as administrator to ask DISM for the real figure.", Classes = { "muted" }, FontSize = 12 });
            }

            mid.Children.Add(row);
        }

        g.Children.Add(mid);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 0, 0), [Grid.ColumnProperty] = 2, MinWidth = 120 };
        var unknown = item.Size == 0 && item.Unreadable;
        right.Children.Add(new TextBlock
        {
            Text = unknown ? "?" : Text.Size(item.Size), FontSize = 20, FontWeight = FontWeight.Bold, TextAlignment = TextAlignment.Right,
            Foreground = item.CanClean ? Good : item.Size > 0 ? TextB : Muted,
        });
        right.Children.Add(new TextBlock
        {
            Text = unknown ? "needs administrator to measure" : item.Unreadable ? "partly unreadable" : item.How == CleanHow.EmptyRecycleBin ? Text.Plural(item.Files, "item") : Text.Plural(item.Files, "file"),
            Classes = { "muted" }, FontSize = 11, TextAlignment = TextAlignment.Right,
        });
        var folder = item.Targets.FirstOrDefault(t => t.Kind != TargetKind.File && Directory.Exists(t.Path))?.Path;
        if (folder is not null && item.How != CleanHow.EmptyRecycleBin)
        {
            var open = new Button { Content = "Open folder", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Right, MinHeight = 28, Padding = new Thickness(8, 2), Margin = new Thickness(0, 6, 0, 0) };
            open.Click += (_, _) => OpenInExplorer(folder, select: false);
            right.Children.Add(open);
        }

        g.Children.Add(right);
        card.Child = g;
        return card;
    }

    /// <summary>Shows what will go and how, asks, then cleans each picked item and measures it again.</summary>
    public async Task<List<(WasteItem Item, CleanResult Result)>> CleanPickedAsync()
    {
        var items = _picked.Where(i => i.CanClean).OrderByDescending(i => i.Size).ToList();
        if (items.Count == 0 || _wasteBusy) return [];
        var lines = items.Select(i => $"{i.Display}: {Text.Size(i.Size)}, {i.HowText}").ToList();
        var permanent = items.Any(i => i.How is CleanHow.Permanent or CleanHow.EmptyRecycleBin or CleanHow.DeliveryOptimization);
        var message = $"{Text.Size(items.Sum(i => i.Size))} in {Text.Plural(items.Count, "item")}. "
                      + (permanent ? "Caches, temp files and the Recycle Bin are deleted permanently (they don't go to the Recycle Bin). " : "")
                      + (items.Any(i => i.How == CleanHow.RecycleBin) ? "Crash dumps go to the Recycle Bin. " : "")
                      + "Files in use are skipped.";
        if (!await Ask(this, new Confirm("Clean up?", message, lines, "Clean up"))) return [];

        _wasteBusy = true;
        UpdateCleanButton();
        _progress.IsVisible = true;
        _status.Text = "Cleaning up…";
        var results = new List<(WasteItem, CleanResult)>();
        try
        {
            foreach (var item in items)
            {
                _status.Text = $"Cleaning up {item.Display}…";
                var r = await Task.Run(() => Cleaner.Clean(item, _places));
                results.Add((item, r));
                await Task.Run(() => WasteFinder.Measure(item, _places));
            }
        }
        finally
        {
            _wasteBusy = false;
            _progress.IsVisible = _busy;
        }

        _picked.Clear();
        ShowWaste();
        var freed = results.Sum(x => x.Item2.Freed);
        var skipped = results.Sum(x => x.Item2.Skipped);
        var notes = results.Where(x => x.Item2.Message is { } m && !m.StartsWith("Freed", StringComparison.Ordinal)).Select(x => $"{x.Item1.Display}: {x.Item2.Message}");
        ShowNotice($"Freed {Text.Size(freed)}." + (skipped > 0 ? $" {Text.Plural(skipped, "file")} in use or not allowed {(skipped == 1 ? "was" : "were")} skipped." : "")
                   + string.Concat(notes.Select(n => " " + n)), good: true);
        _status.Text = $"Cleaned up {Text.Plural(results.Count, "item")}, freed {Text.Size(freed)}.";
        return results;
    }
}
