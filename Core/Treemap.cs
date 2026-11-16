namespace pxdSpaceAnalyzer.Core;

public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Area => W * H;
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + W && y < Y + H;
}

/// <summary>One rectangle of the treemap: a file, a folder's smaller files, or a folder (drawn as a frame around its contents).</summary>
public sealed record Tile(Box Box, string Name, long Size, FileKind Kind, DirNode? Folder, DirNode Owner, int Depth, bool IsFolder);

/// <summary>
/// Squarified treemap (Bruls, Huizing and van Wijk): rows of rectangles laid along the shorter side, each row kept as
/// close to square as possible. <see cref="Layout"/> lays out a whole folder tree, down to rectangles a few pixels wide.
/// </summary>
public static class Treemap
{
    /// <summary>Rectangles for <paramref name="weights"/> inside <paramref name="area"/>, in the same order. Zero weights get empty boxes.</summary>
    public static Box[] Squarify(IReadOnlyList<double> weights, Box area)
    {
        var result = new Box[weights.Count];
        var order = Enumerable.Range(0, weights.Count).Where(i => weights[i] > 0).OrderByDescending(i => weights[i]).ToList();
        var total = order.Sum(i => weights[i]);
        if (order.Count == 0 || total <= 0 || area.W <= 0 || area.H <= 0) return result;
        var scale = area.Area / total;

        var rest = area;
        var start = 0;
        while (start < order.Count)
        {
            var side = Math.Min(rest.W, rest.H);
            if (side <= 1e-9) break;   // no room left (rounding): the rest stay empty
            var rowSum = 0.0;
            var end = start;
            var best = double.MaxValue;
            // Grow the row while it makes the worst aspect ratio better.
            while (end < order.Count)
            {
                var a = weights[order[end]] * scale;
                var next = Worst(order, weights, start, end, rowSum + a, side, scale);
                if (next > best && end > start) break;
                best = next;
                rowSum += a;
                end++;
            }

            // Lay the row along the shorter side.
            var thick = rowSum / side;
            var pos = 0.0;
            var horizontal = rest.W >= rest.H;   // the row is a column on the left (wide area) or a row on top (tall area)
            for (var k = start; k < end; k++)
            {
                var len = weights[order[k]] * scale / thick;
                result[order[k]] = horizontal
                    ? new Box(rest.X, rest.Y + pos, thick, len)
                    : new Box(rest.X + pos, rest.Y, len, thick);
                pos += len;
            }

            rest = horizontal
                ? new Box(rest.X + thick, rest.Y, Math.Max(0, rest.W - thick), rest.H)
                : new Box(rest.X, rest.Y + thick, rest.W, Math.Max(0, rest.H - thick));
            start = end;
        }

        return result;
    }

    private static double Worst(List<int> order, IReadOnlyList<double> weights, int start, int end, double sum, double side, double scale)
    {
        double max = 0, min = double.MaxValue;
        for (var k = start; k <= end; k++)
        {
            var a = weights[order[k]] * scale;
            max = Math.Max(max, a);
            min = Math.Min(min, a);
        }

        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    /// <summary>Lays out <paramref name="root"/> (folders, files kept by name, each folder's smaller files) inside <paramref name="area"/>.</summary>
    public static List<Tile> Layout(DirNode root, Box area, double minSide = 3, int maxTiles = 60000)
    {
        var tiles = new List<Tile>();
        var queue = new Queue<(DirNode Node, Box Box, int Depth)>();
        queue.Enqueue((root, area, 0));
        while (queue.Count > 0 && tiles.Count < maxTiles)
        {
            var (node, box, depth) = queue.Dequeue();
            var inner = depth == 0 ? box : Shrink(box, depth);
            if (inner.W < minSide || inner.H < minSide) continue;

            var weights = new List<double>();
            var what = new List<object>();
            foreach (var d in node.Dirs)
            {
                if (d.Alloc <= 0) continue;
                weights.Add(d.Alloc);
                what.Add(d);
            }

            foreach (var f in node.Files)
            {
                if (f.Alloc <= 0) continue;
                weights.Add(f.Alloc);
                what.Add(f);
            }

            if (node.SmallAlloc > 0)
            {
                weights.Add(node.SmallAlloc);
                what.Add(node);
            }

            var boxes = Squarify(weights, inner);
            for (var i = 0; i < boxes.Length; i++)
            {
                var b = boxes[i];
                if (b.W < 1 || b.H < 1) continue;
                switch (what[i])
                {
                    case DirNode d when !ReferenceEquals(d, node):
                        tiles.Add(new Tile(b, d.Name, d.Alloc, FileTypes.Folder, d, node, depth + 1, true));
                        if (b.W >= minSide * 2 && b.H >= minSide * 2) queue.Enqueue((d, b, depth + 1));
                        break;
                    case FileEntry f:
                        tiles.Add(new Tile(b, f.Name, f.Alloc, FileTypes.OfName(f.Name), null, node, depth + 1, false));
                        break;
                    default:
                        tiles.Add(new Tile(b, $"{node.SmallCount:N0} smaller files", node.SmallAlloc, FileTypes.Folder, null, node, depth + 1, false));
                        break;
                }
            }
        }

        return tiles;
    }

    /// <summary>A folder's frame: a little padding, and a strip on top for its name when there's room.</summary>
    private static Box Shrink(Box b, int depth)
    {
        var pad = depth <= 2 ? 1.5 : 1;
        var head = b.H > 40 && b.W > 60 ? 16 : 0;
        return new Box(b.X + pad, b.Y + pad + head, Math.Max(0, b.W - 2 * pad), Math.Max(0, b.H - 2 * pad - head));
    }

    /// <summary>The worst width/height ratio among <paramref name="boxes"/> (1 = all square). Used by the self-test.</summary>
    public static double WorstAspect(IEnumerable<Box> boxes) =>
        boxes.Where(b => b.W > 0 && b.H > 0).Select(b => Math.Max(b.W / b.H, b.H / b.W)).DefaultIfEmpty(1).Max();
}
