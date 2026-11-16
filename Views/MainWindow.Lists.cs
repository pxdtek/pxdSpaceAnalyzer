using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using pxdSpaceAnalyzer.Core;

namespace pxdSpaceAnalyzer.Views;

public sealed partial class MainWindow
{
    private readonly ListBox _files = new() { Classes = { "log" } };
    private readonly ListBox _types = new() { Classes = { "log" } };
    private readonly TextBlock _filesPath = new() { FontFamily = Mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _filesOpen = new() { Content = "Show in Explorer", IsEnabled = false };
    private readonly Button _filesDelete = new() { Content = "Delete to Recycle Bin…", IsEnabled = false };

    public int FilesShown => _files.ItemCount;
    public int TypesShown => _types.ItemCount;

    private Control BuildFilesPage()
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,100,100,140"), Margin = new Thickness(13, 0, 13, 6) };
        void H(string t, int c, bool right = false) => head.Children.Add(new TextBlock { Text = t, Classes = { "label" }, [Grid.ColumnProperty] = c, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 6, 0) });
        H("#", 0);
        H($"THE {Scanner.TopFileCount} BIGGEST FILES", 1);
        H("SIZE", 2, true);
        H("ON DISK", 3, true);
        H("MODIFIED", 4, true);
        g.Children.Add(head);

        _files.ItemTemplate = new FuncDataTemplate<FileHit>((f, _) => f is null ? new Panel() : FileLine(f), supportsRecycling: false);
        _files.SelectionChanged += (_, _) =>
        {
            var f = _files.SelectedItem as FileHit;
            _filesPath.Text = f?.Path ?? "Pick a file to show it in Explorer or delete it.";
            _filesOpen.IsEnabled = f is not null;
            _filesDelete.IsEnabled = f is not null && !IsProtected(f.Path);
        };
        _files.DoubleTapped += (_, _) =>
        {
            if (_files.SelectedItem is FileHit f) OpenInExplorer(f.Path, select: true);
        };
        Grid.SetRow(_files, 1);
        g.Children.Add(_files);

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0), [Grid.RowProperty] = 2 };
        _filesPath.Text = "Pick a file to show it in Explorer or delete it.";
        actions.Children.Add(_filesPath);
        var bs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [Grid.ColumnProperty] = 1 };
        _filesOpen.Click += (_, _) =>
        {
            if (_files.SelectedItem is FileHit f) OpenInExplorer(f.Path, select: true);
        };
        _filesDelete.Click += async (_, _) =>
        {
            if (_files.SelectedItem is FileHit f) await DeleteFileHitAsync(f);
        };
        bs.Children.Add(_filesOpen);
        bs.Children.Add(_filesDelete);
        actions.Children.Add(bs);
        g.Children.Add(actions);
        return g;
    }

    private Control FileLine(FileHit f)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,100,100,140"), Margin = new Thickness(0, 3), Background = Brushes.Transparent };
        var index = (_result?.TopFiles.IndexOf(f) ?? -1) + 1;
        g.Children.Add(new TextBlock { Text = index.ToString(), Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var mid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), [Grid.ColumnProperty] = 1 };
        mid.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = Brush(FileTypes.OfName(f.Name).Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var names = new StackPanel { [Grid.ColumnProperty] = 1 };
        names.Children.Add(new TextBlock { Text = f.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        names.Children.Add(new TextBlock { Text = f.Folder, Classes = { "muted" }, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        mid.Children.Add(names);
        g.Children.Add(mid);
        TextBlock Num(string s, int c, bool strong = false) => new()
        {
            Text = s, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), FontSize = 12.5,
            Foreground = strong ? TextB : Muted, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal, [Grid.ColumnProperty] = c,
        };
        g.Children.Add(Num(Text.Size(f.Size), 2));
        g.Children.Add(Num(Text.Size(f.Alloc), 3, true));
        g.Children.Add(Num(Text.Date(f.LastWrite), 4));
        ToolTip.SetTip(g, f.Path);
        return g;
    }

    public void RefreshFiles() => _files.ItemsSource = _result?.TopFiles.ToList();

    private async Task DeleteFileHitAsync(FileHit f)
    {
        if (_result is null) return;
        var folder = _result.Root.Find(f.Folder);
        var entry = folder?.Files.FirstOrDefault(e => e.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase));
        if (folder is not null && entry is { } e)
        {
            _selectedRow = new TreeRow { Owner = folder, File = e, Depth = folder.Depth + 1 };
            await DeleteSelectedAsync();
        }
    }

    // ---------- File types ----------

    private Control BuildTypesPage()
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("110,180,*,100,100"), Margin = new Thickness(13, 0, 13, 6) };
        void H(string t, int c, bool right = false) => head.Children.Add(new TextBlock { Text = t, Classes = { "label" }, [Grid.ColumnProperty] = c, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 6, 0) });
        H("EXTENSION", 0);
        H("KIND", 1);
        H("SHARE", 2);
        H("ON DISK", 3, true);
        H("FILES", 4, true);
        g.Children.Add(head);
        _types.ItemTemplate = new FuncDataTemplate<ExtStat>((e, _) => e is null ? new Panel() : TypeLine(e), supportsRecycling: false);
        Grid.SetRow(_types, 1);
        g.Children.Add(_types);
        return g;
    }

    private Control TypeLine(ExtStat e)
    {
        var total = Math.Max(1, _result?.Root.Alloc ?? 1);
        var kind = FileTypes.Of(e.Ext);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,180,*,100,100"), Margin = new Thickness(0, 4), Background = Brushes.Transparent };
        g.Children.Add(new TextBlock { Text = e.Ext.Length == 0 ? "(none)" : e.Ext, FontFamily = Mono, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
        var k = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [Grid.ColumnProperty] = 1 };
        k.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = Brush(kind.Color), VerticalAlignment = VerticalAlignment.Center });
        k.Children.Add(new TextBlock { Text = kind.Name, Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(k);
        var share = (double)e.Alloc / total;
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,56"), Margin = new Thickness(0, 0, 10, 0), [Grid.ColumnProperty] = 2 };
        bar.Children.Add(Bar(share, Brush(kind.Color), 8));
        bar.Children.Add(new TextBlock { Text = Text.Percent(share), Classes = { "muted" }, FontSize = 11, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 });
        g.Children.Add(bar);
        g.Children.Add(new TextBlock { Text = Text.Size(e.Alloc), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 6, 0), [Grid.ColumnProperty] = 3 });
        g.Children.Add(new TextBlock { Text = Text.Count(e.Count), Classes = { "muted" }, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), [Grid.ColumnProperty] = 4 });
        return g;
    }

    public void RefreshTypes() => _types.ItemsSource = _result?.Extensions.Take(500).ToList();
}
