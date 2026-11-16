using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using pxdSpaceAnalyzer.Core;

namespace pxdSpaceAnalyzer.Views;

/// <summary>
/// Draws a folder as a squarified treemap: every file a rectangle sized by the space it takes, coloured by its kind;
/// folders are dark frames with their name on top. Click a folder to zoom into it, right-click to go up.
/// </summary>
public sealed class TreemapView : Control
{
    private DirNode? _root;
    private List<Tile> _tiles = [];
    private Size _laidOut;
    private Tile? _hover;
    private readonly Dictionary<string, IBrush> _fills = [];
    private static readonly IPen FileEdge = new Pen(new SolidColorBrush(Color.Parse("#AA0B0E13")), 1);
    private static readonly IPen FolderEdge = new Pen(new SolidColorBrush(Color.Parse("#2A3142")), 1);
    private static readonly IPen HoverEdge = new Pen(Brushes.White, 2);
    private static readonly IBrush FolderFill = new SolidColorBrush(Color.Parse("#141821"));
    private static readonly IBrush HeadText = new SolidColorBrush(Color.Parse("#C9CED9"));
    private static readonly Typeface Face = new("Segoe UI");

    /// <summary>Raised when the pointer moves over a tile (null when it leaves).</summary>
    public event Action<Tile?>? Hovered;

    /// <summary>Raised when a folder is clicked (zoom in) or right-clicked (zoom out: its parent).</summary>
    public event Action<DirNode>? ZoomTo;

    public DirNode? Root
    {
        get => _root;
        set
        {
            _root = value;
            _laidOut = default;
            InvalidateVisual();
        }
    }

    public IReadOnlyList<Tile> Tiles
    {
        get
        {
            EnsureLayout(Bounds.Size);
            return _tiles;
        }
    }

    public TreemapView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    private void EnsureLayout(Size size)
    {
        if (_root is null || size.Width < 4 || size.Height < 4)
        {
            _tiles = [];
            return;
        }

        if (_laidOut == size) return;
        _tiles = Treemap.Layout(_root, new Box(0, 0, size.Width, size.Height), minSide: 3, maxTiles: 25000);
        _laidOut = size;
    }

    /// <summary>The layout is done again (after a delete changed the sizes).</summary>
    public void Relayout()
    {
        _laidOut = default;
        InvalidateVisual();
    }

    private IBrush Fill(FileKind k)
    {
        if (!_fills.TryGetValue(k.Color, out var b)) _fills[k.Color] = b = new SolidColorBrush(Color.Parse(k.Color));
        return b;
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#0B0E13")), new Rect(size));
        EnsureLayout(size);
        foreach (var t in _tiles)
        {
            var r = new Rect(t.Box.X, t.Box.Y, t.Box.W, t.Box.H);
            if (t.IsFolder)
            {
                ctx.DrawRectangle(FolderFill, FolderEdge, r);
                if (t.Box.W > 60 && t.Box.H > 40)
                {
                    var label = new FormattedText($"{t.Name}  {Text.Size(t.Size)}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, HeadText)
                    {
                        MaxTextWidth = Math.Max(1, t.Box.W - 8), MaxTextHeight = 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
                    };
                    ctx.DrawText(label, new Point(t.Box.X + 4, t.Box.Y + 1));
                }
            }
            else
            {
                ctx.DrawRectangle(Fill(t.Kind), t.Box.W > 3 && t.Box.H > 3 ? FileEdge : null, r);
            }
        }

        if (_hover is { } h)
            ctx.DrawRectangle(null, HoverEdge, new Rect(h.Box.X + 1, h.Box.Y + 1, Math.Max(0, h.Box.W - 2), Math.Max(0, h.Box.H - 2)));
    }

    /// <summary>The smallest tile under a point (a file, or a folder's frame).</summary>
    public Tile? HitTest(Point p)
    {
        Tile? best = null;
        foreach (var t in Tiles)
            if (t.Box.Contains(p.X, p.Y) && (best is null || t.Depth >= best.Depth))
                best = t;
        return best;
    }

    /// <summary>The folder one level below the shown root that holds the point: what a click zooms into.</summary>
    public DirNode? FolderAt(Point p)
    {
        var t = HitTest(p);
        if (t is null || _root is null) return null;
        var folder = t.Folder ?? t.Owner;
        while (folder.Parent is not null && !ReferenceEquals(folder.Parent, _root)) folder = folder.Parent;
        return ReferenceEquals(folder, _root) ? null : folder;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var t = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(t, _hover))
        {
            _hover = t;
            InvalidateVisual();
            Hovered?.Invoke(t);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
        Hovered?.Invoke(null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsRightButtonPressed)
        {
            if (_root?.Parent is { } up) ZoomTo?.Invoke(up);
        }
        else if (pt.Properties.IsLeftButtonPressed && FolderAt(e.GetPosition(this)) is { } f)
        {
            ZoomTo?.Invoke(f);
        }

        e.Handled = true;
    }
}
