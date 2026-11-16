using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using pxdSpaceAnalyzer.Core;

namespace pxdSpaceAnalyzer.Views;

public sealed partial class MainWindow
{
    private readonly TreemapView _map = new();
    private readonly TextBlock _mapPath = new() { FontFamily = Mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _mapHover = new() { Classes = { "muted" }, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _mapUp = new() { Content = "Up", IsEnabled = false };
    private DirNode? _mapRoot;

    public TreemapView Map => _map;
    public DirNode? MapRoot => _mapRoot;

    private Control BuildMapPage()
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        _mapUp.Click += (_, _) =>
        {
            if (_mapRoot?.Parent is { } up) ZoomMap(up);
        };
        ToolTip.SetTip(_mapUp, "Back to the folder above (or right-click the map).");
        top.Children.Add(_mapUp);
        _mapPath.Margin = new Thickness(10, 0);
        Grid.SetColumn(_mapPath, 1);
        top.Children.Add(_mapPath);
        var tree = new Button { Content = "Show in folders", [Grid.ColumnProperty] = 2 };
        tree.Click += (_, _) =>
        {
            if (_mapRoot is { } r)
            {
                Reveal(r);
                ShowPage(AppPage.Folders);
            }
        };
        top.Children.Add(tree);
        g.Children.Add(top);

        var frame = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = Brush("#272C39"), BorderThickness = new Thickness(1), Child = _map, [Grid.RowProperty] = 1 };
        g.Children.Add(frame);
        _map.Hovered += t => _mapHover.Text = t is null ? "Click a folder to zoom in, right-click to go back up."
            : t.IsFolder ? $"{t.Folder!.FullPath}  ·  {Text.Size(t.Size)}  ·  {Text.Plural(t.Folder.FileCount, "file")}"
            : $"{System.IO.Path.Join(t.Owner.FullPath, t.Name)}  ·  {Text.Size(t.Size)}  ·  {t.Kind.Name}";
        _map.ZoomTo += ZoomMap;
        _mapHover.Text = "Click a folder to zoom in, right-click to go back up.";
        _mapHover.Margin = new Thickness(0, 8, 0, 4);
        Grid.SetRow(_mapHover, 2);
        g.Children.Add(_mapHover);

        var legend = new WrapPanel { Orientation = Orientation.Horizontal, [Grid.RowProperty] = 3 };
        foreach (var k in FileTypes.All.Append(FileTypes.Folder))
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 16, 2) };
            item.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = Brush(k.Color), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = k.Name, FontSize = 11.5, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(item);
        }

        g.Children.Add(legend);
        return g;
    }

    public void ZoomMap(DirNode node)
    {
        _mapRoot = node;
        if (_page != AppPage.Treemap) ShowPage(AppPage.Treemap);
        else RefreshMap();
    }

    private void RefreshMap()
    {
        _map.Root = _mapRoot;
        _map.Relayout();
        _mapPath.Text = _mapRoot is null ? "" : $"{_mapRoot.FullPath}   {Text.Size(_mapRoot.Alloc)}";
        _mapUp.IsEnabled = _mapRoot?.Parent is not null;
    }
}
