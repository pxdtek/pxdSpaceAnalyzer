using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using pxdSpaceAnalyzer.Core;

namespace pxdSpaceAnalyzer.Views;

/// <summary>One line of the folder tree: a folder, a file kept by name, or a folder's smaller files.</summary>
public sealed class TreeRow
{
    public required DirNode Owner { get; init; }
    public DirNode? Folder { get; init; }
    public FileEntry? File { get; init; }
    public int Depth { get; init; }
    public bool IsSmall => Folder is null && File is null;

    public string Name => Folder?.Name ?? File?.Name ?? $"{Text.Count(Owner.SmallCount)} smaller files";
    public long Size => Folder?.Size ?? File?.Size ?? Owner.SmallSize;
    public long Alloc => Folder?.Alloc ?? File?.Alloc ?? Owner.SmallAlloc;
    public long Files => Folder?.FileCount ?? (File is null ? Owner.SmallCount : 1);
    public DateTime LastWrite => Folder?.LastWrite ?? File?.LastWrite ?? DateTime.MinValue;
    public string FullPath => Folder?.FullPath ?? (File is { } f ? System.IO.Path.Join(Owner.FullPath, f.Name) : Owner.FullPath);
}

public sealed partial class MainWindow
{
    private static readonly string[] SortNames = ["Name", "Size", "On disk", "Files", "Modified"];
    private const string Columns = "*,130,96,96,90,140";

    private readonly HashSet<DirNode> _expanded = [];
    private readonly ListBox _tree = new() { Classes = { "log" } };
    private readonly TextBlock _treePath = new() { FontFamily = Mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _treeOpen = new() { Content = "Open in Explorer", IsEnabled = false };
    private readonly Button _treeMap = new() { Content = "Show in treemap", IsEnabled = false };
    private readonly Button _treeDelete = new() { Content = "Delete to Recycle Bin…", IsEnabled = false };
    private readonly Dictionary<int, TextBlock> _sortHeads = [];
    private TreeRow? _selectedRow;
    private bool _refreshingTree;

    public IReadOnlyList<TreeRow> TreeRows => (_tree.ItemsSource as IReadOnlyList<TreeRow>) ?? [];

    private Control BuildTreePage()
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(13, 0, 13, 6) };
        for (var i = 0; i < SortNames.Length; i++)
        {
            var col = i;
            var t = new TextBlock { Text = SortNames[i], Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center };
            _sortHeads[i] = t;
            var b = new Button
            {
                Content = t, Classes = { "ghost" }, Padding = new Thickness(4, 2), MinHeight = 26,
                HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            };
            ToolTip.SetTip(b, $"Sort by {SortNames[i].ToLowerInvariant()}");
            b.Click += (_, _) => SortTree(col);
            Grid.SetColumn(b, i == 0 ? 0 : i + 1);
            head.Children.Add(b);
        }

        head.Children.Add(new TextBlock { Text = "% OF FOLDER", Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), [Grid.ColumnProperty] = 1 });
        g.Children.Add(head);

        _tree.ItemTemplate = new FuncDataTemplate<TreeRow>((r, _) => r is null ? new Panel() : TreeLine(r), supportsRecycling: false);
        _tree.SelectionChanged += (_, _) =>
        {
            if (_refreshingTree) return;
            _selectedRow = _tree.SelectedItem as TreeRow;
            ShowTreeSelection();
        };
        _tree.DoubleTapped += (_, _) =>
        {
            if (_selectedRow?.Folder is { } f) Toggle(f);
        };
        _tree.KeyDown += (_, e) =>
        {
            if (_selectedRow?.Folder is not { } f) return;
            if (e.Key == Key.Right && !_expanded.Contains(f)) { Toggle(f); e.Handled = true; }
            else if (e.Key == Key.Left && _expanded.Contains(f)) { Toggle(f); e.Handled = true; }
            else if (e.Key == Key.Delete) { _ = DeleteSelectedAsync(); e.Handled = true; }
        };
        _tree.ContextMenu = TreeMenu();
        Grid.SetRow(_tree, 1);
        g.Children.Add(_tree);

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(_treePath);
        var bs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [Grid.ColumnProperty] = 1 };
        _treeOpen.Click += (_, _) => OpenRow(_selectedRow);
        _treeMap.Click += (_, _) =>
        {
            if (_selectedRow is { } r) ZoomMap(r.Folder ?? r.Owner);
        };
        _treeDelete.Click += async (_, _) => await DeleteSelectedAsync();
        ToolTip.SetTip(_treeDelete, "Moves it to the Recycle Bin (asks first). Windows, program and profile folders can't be deleted here.");
        bs.Children.Add(_treeOpen);
        bs.Children.Add(_treeMap);
        bs.Children.Add(_treeDelete);
        actions.Children.Add(bs);
        Grid.SetRow(actions, 2);
        g.Children.Add(actions);
        return g;
    }

    private ContextMenu TreeMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open in Explorer" };
        open.Click += (_, _) => OpenRow(_selectedRow);
        var map = new MenuItem { Header = "Show in treemap" };
        map.Click += (_, _) =>
        {
            if (_selectedRow is { } r) ZoomMap(r.Folder ?? r.Owner);
        };
        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += async (_, _) =>
        {
            if (_selectedRow is { } r && Clipboard is { } cb) await cb.SetTextAsync(r.FullPath);
        };
        var del = new MenuItem { Header = "Delete to Recycle Bin…" };
        del.Click += async (_, _) => await DeleteSelectedAsync();
        menu.Items.Add(open);
        menu.Items.Add(map);
        menu.Items.Add(copy);
        menu.Items.Add(new Separator());
        menu.Items.Add(del);
        return menu;
    }

    private Control TreeLine(TreeRow r)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(0, 3), Background = Brushes.Transparent };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(r.Depth * 18, 0, 0, 0), Spacing = 4 };
        if (r.Folder is { } f && (f.Dirs.Count > 0 || f.Files.Length > 0 || f.SmallCount > 0))
        {
            var chevron = new Button
            {
                Content = new TextBlock { Text = _expanded.Contains(f) ? "" : "", FontFamily = Glyphs, FontSize = 10 },
                Classes = { "ghost" }, Padding = new Thickness(2), MinHeight = 0, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center,
            };
            chevron.Click += (_, _) => Toggle(f);
            name.Children.Add(chevron);
        }
        else
        {
            name.Children.Add(new Border { Width = 20 });
        }

        var (glyph, color) = r.Folder is { } d
            ? (d.IsLink ? "" : "", d.IsLink ? Muted : d.Unreadable ? Warn : Brush("#F5C451"))
            : r.IsSmall ? ("", Muted) : ("", Brush(FileTypes.OfName(r.Name).Color));
        name.Children.Add(new TextBlock { Text = glyph, FontFamily = Glyphs, FontSize = 14, Foreground = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        name.Children.Add(new TextBlock
        {
            Text = r.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = r.IsSmall ? Muted : TextB, FontWeight = r.Folder is not null && r.Depth == 0 ? FontWeight.Bold : FontWeight.Normal,
        });
        if (r.Folder is { IsLink: true }) name.Children.Add(new TextBlock { Text = "link, not followed", Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        else if (r.Folder is { Unreadable: true }) name.Children.Add(new TextBlock { Text = "couldn't be read", Foreground = Warn, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        g.Children.Add(name);

        var parent = r.Folder?.Parent ?? (r.Folder is null ? r.Owner : null);
        var share = parent is { Alloc: > 0 } ? (double)r.Alloc / parent.Alloc : 1;
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,44"), Margin = new Thickness(8, 0, 6, 0), [Grid.ColumnProperty] = 1 };
        bar.Children.Add(Bar(share, share > 0.5 ? Brush("#19F58C") : share > 0.2 ? Brush("#7BE0A8") : Brush("#3E8E6A"), 8));
        bar.Children.Add(new TextBlock { Text = Text.Percent(share), Classes = { "muted" }, FontSize = 11, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 });
        g.Children.Add(bar);
        TextBlock Num(string s, int col, bool strong = false) => new()
        {
            Text = s, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Margin = new Thickness(0, 0, 6, 0),
            Foreground = strong ? TextB : Muted, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal, [Grid.ColumnProperty] = col,
        };
        g.Children.Add(Num(Text.Size(r.Size), 2));
        g.Children.Add(Num(Text.Size(r.Alloc), 3, strong: true));
        g.Children.Add(Num(Text.Count(r.Files), 4));
        g.Children.Add(Num(Text.Date(r.LastWrite), 5));
        ToolTip.SetTip(g, r.FullPath);
        return g;
    }

    private void SortTree(int col)
    {
        if (_settings.TreeSort == col) _settings.TreeDescending = !_settings.TreeDescending;
        else
        {
            _settings.TreeSort = col;
            _settings.TreeDescending = col != 0;
        }

        _settings.Save();
        RefreshTree();
    }

    public void SetTreeSort(int col, bool descending)
    {
        _settings.TreeSort = col;
        _settings.TreeDescending = descending;
        RefreshTree();
    }

    private IEnumerable<TreeRow> Children(DirNode n, int depth)
    {
        var rows = n.Dirs.Select(d => new TreeRow { Owner = n, Folder = d, Depth = depth })
            .Concat(n.Files.Select(f => new TreeRow { Owner = n, File = f, Depth = depth }));
        IEnumerable<TreeRow> sorted = _settings.TreeSort switch
        {
            0 => rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            1 => rows.OrderBy(r => r.Size),
            3 => rows.OrderBy(r => r.Files),
            4 => rows.OrderBy(r => r.LastWrite),
            _ => rows.OrderBy(r => r.Alloc),
        };
        if (_settings.TreeDescending) sorted = sorted.Reverse();
        foreach (var r in sorted) yield return r;
        if (n.SmallCount > 0) yield return new TreeRow { Owner = n, Depth = depth };
    }

    public void RefreshTree()
    {
        for (var i = 0; i < SortNames.Length; i++)
            _sortHeads[i].Text = SortNames[i].ToUpperInvariant() + (_settings.TreeSort == i ? (_settings.TreeDescending ? "  ▼" : "  ▲") : "");
        if (_result is not { } res)
        {
            _tree.ItemsSource = null;
            return;
        }

        var list = new List<TreeRow>();
        void Add(TreeRow r)
        {
            list.Add(r);
            if (r.Folder is { } f && _expanded.Contains(f))
                foreach (var c in Children(f, r.Depth + 1)) Add(c);
        }

        Add(new TreeRow { Owner = res.Root, Folder = res.Root, Depth = 0 });
        _refreshingTree = true;
        _tree.ItemsSource = list;
        var keep = _selectedRow is null ? null : list.FirstOrDefault(r => SameRow(r, _selectedRow));
        _tree.SelectedItem = keep;
        _selectedRow = keep;
        _refreshingTree = false;
        ShowTreeSelection();
    }

    private static bool SameRow(TreeRow a, TreeRow b) =>
        ReferenceEquals(a.Owner, b.Owner) && ReferenceEquals(a.Folder, b.Folder) && Nullable.Equals(a.File, b.File);

    public void Toggle(DirNode f)
    {
        if (!_expanded.Remove(f)) _expanded.Add(f);
        RefreshTree();
    }

    /// <summary>Opens every folder down to <paramref name="target"/> and selects it.</summary>
    public void Reveal(DirNode target)
    {
        for (var p = target.Parent; p is not null; p = p.Parent) _expanded.Add(p);
        _selectedRow = new TreeRow { Owner = target.Parent ?? target, Folder = target, Depth = target.Depth };
        RefreshTree();
        if (_tree.SelectedItem is { } s) _tree.ScrollIntoView(s);
    }

    public void SelectRow(TreeRow row)
    {
        _selectedRow = TreeRows.FirstOrDefault(r => SameRow(r, row)) ?? row;
        _tree.SelectedItem = _selectedRow;
        ShowTreeSelection();
    }

    public TreeRow? SelectedRow => _selectedRow;

    private void ShowTreeSelection()
    {
        var r = _selectedRow;
        _treePath.Text = r is null ? "Pick a folder or file to open it or delete it. Double-click a folder to open it here." : r.FullPath;
        _treePath.Foreground = r is null ? Muted : TextB;
        _treeOpen.IsEnabled = r is not null;
        _treeMap.IsEnabled = r is not null;
        _treeDelete.IsEnabled = r is not null && !r.IsSmall && !IsProtected(r.FullPath);
    }

    private static void OpenRow(TreeRow? r)
    {
        if (r is null) return;
        if (r.Folder is { } f) OpenInExplorer(f.FullPath, select: false);
        else if (r.File is not null) OpenInExplorer(r.FullPath, select: true);
        else OpenInExplorer(r.Owner.FullPath, select: false);
    }

    /// <summary>Asks, then moves the selected folder or file to the Recycle Bin and takes it out of the tree.</summary>
    public async Task<bool> DeleteSelectedAsync()
    {
        if (_selectedRow is not { IsSmall: false } r || _busy) return false;
        var path = r.FullPath;
        if (IsProtected(path))
        {
            ShowNotice($"{path} is a folder Windows, programs or your profile need, so it can't be deleted here.");
            return false;
        }

        var what = r.Folder is { } f ? $"the folder {f.Name} ({Text.Size(f.Alloc)}, {Text.Plural(f.FileCount, "file")})" : $"the file {r.Name} ({Text.Size(r.Alloc)})";
        var ok = await Ask(this, new Confirm("Delete to Recycle Bin?", $"This moves {what} to the Recycle Bin. You can put it back from there until the Recycle Bin is emptied.", [path], "Move to Recycle Bin"));
        if (!ok) return false;
        var done = await Task.Run(() => RecycleAction(path));
        if (!done)
        {
            ShowNotice($"Couldn't delete {path}: it may be in use, or need administrator rights.");
            return false;
        }

        if (r.Folder is { } folder) Scanner.Remove(folder);
        else if (r.File is { } file) Scanner.RemoveFile(r.Owner, file);
        _result?.TopFiles.RemoveAll(h => Places.Inside(h.Path, path));
        _selectedRow = null;
        RefreshTree();
        RefreshFiles();
        RefreshMap();
        SetScanTitle();
        _status.Text = $"Moved {path} to the Recycle Bin.";
        return true;
    }
}
