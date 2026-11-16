using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace pxdSpaceAnalyzer;

/// <summary>
/// pxd Space Analyzer's icon: a treemap (blocks sized by what they hold) with one block lit up in yellow, on a
/// lime-to-green tile. Drawn from vector shapes so it stays crisp at every size; used for the window and the exe's .ico.
/// </summary>
public static class BrandIcon
{
    public static Control Build(double size)
    {
        var a = new Canvas { Width = 100, Height = 100 };
        IBrush B(string c) => new SolidColorBrush(Color.Parse(c));
        void Block(double x, double y, double w, double h, string color) =>
            a.Children.Add(new Border { Width = w, Height = h, CornerRadius = new CornerRadius(5), Background = B(color), [Canvas.LeftProperty] = x, [Canvas.TopProperty] = y });

        // The biggest folder, then smaller ones beside it, like the treemap view.
        Block(8, 8, 50, 84, "#FFFFFF");
        Block(62, 8, 30, 46, "#E6FFFFFF");
        Block(62, 58, 14, 34, "#5A3600");
        Block(80, 58, 12, 16, "#CCFFFFFF");
        Block(80, 78, 12, 14, "#B3FFFFFF");
        // The big block holds a few files of its own.
        Block(15, 62, 36, 23, "#8A5200");
        Block(15, 15, 17, 40, "#B77400");
        return Tile(size, "#FFD84D", "#C27A00", a, 0.12);
    }
    public static RenderTargetBitmap Render(int size)
    {
        var c = Build(size);
        c.Measure(new Size(size, size));
        c.Arrange(new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(c);
        return bitmap;
    }

    public static WindowIcon Window(int size = 64) => new(Render(size));

    /// <summary>Writes a multi-size .ico (PNG entries) for the exe, or a 256 px PNG when the path ends in .png.</summary>
    public static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            using var png = File.Create(path);
            Render(256).Save(png, PngBitmapEncoderOptions.Default);
            return;
        }

        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(s =>
        {
            using var ms = new MemoryStream();
            Render(s).Save(ms, PngBitmapEncoderOptions.Default);
            return ms.ToArray();
        }).ToList();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var b = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(b);
            w.Write(b);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }

        foreach (var img in images) w.Write(img);
    }

    private static Grid Tile(double size, string from, string to, Canvas art, double margin)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.22),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(from), 0), new GradientStop(Color.Parse(to), 1) },
            },
        });
        g.Children.Add(new Viewbox { Margin = new Thickness(size * margin), Child = art });
        return g;
    }
}
