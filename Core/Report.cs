namespace pxdSpaceAnalyzer.Core;

/// <summary>--report PATH [--waste]: scans PATH (read only) and prints the biggest folders, files and file types to the console.</summary>
public static class Report
{
    public static int Run(string path, bool waste)
    {
        Native.AttachConsole(-1);
        var o = Console.Out;
        o.WriteLine();
        if (!Directory.Exists(path))
        {
            o.WriteLine($"There's no folder at {path}.");
            return 2;
        }

        var r = Scanner.Scan(path);
        var root = r.Root;
        o.WriteLine($"pxd Space Analyzer: {root.FullPath}");
        o.WriteLine($"  {Text.Size(root.Size)} ({Text.Size(root.Alloc)} on disk) in {Text.Plural(root.FileCount, "file")} and {Text.Plural(root.DirCount, "folder")}, read in {r.Elapsed.TotalSeconds:0.00} s"
                    + $" ({root.FileCount / Math.Max(0.001, r.Elapsed.TotalSeconds):N0} files/s)");
        if (r.UnreadableCount > 0) o.WriteLine($"  {Text.Plural(r.UnreadableCount, "folder")} couldn't be read{(Elevation.IsElevated ? "" : " (run as administrator)")}.");
        if (r.LinksSkipped > 0) o.WriteLine($"  {Text.Plural(r.LinksSkipped, "junction or folder link")} not followed.");
        o.WriteLine();
        o.WriteLine("  Biggest folders");
        foreach (var d in root.Dirs.Take(15))
            o.WriteLine($"  {Text.Size(d.Alloc),10}  {Text.Percent(root.Alloc > 0 ? (double)d.Alloc / root.Alloc : 0),6}  {d.Name}{(d.IsLink ? "  (link)" : d.Unreadable ? "  (couldn't be read)" : "")}");
        o.WriteLine();
        o.WriteLine("  Biggest files");
        foreach (var f in r.TopFiles.Take(10))
            o.WriteLine($"  {Text.Size(f.Alloc),10}  {f.Path}");
        o.WriteLine();
        o.WriteLine("  File types");
        foreach (var e in r.Extensions.Take(10))
            o.WriteLine($"  {Text.Size(e.Alloc),10}  {(e.Ext.Length == 0 ? "(none)" : e.Ext),-10} {Text.Plural(e.Count, "file")}");
        if (waste)
        {
            o.WriteLine();
            o.WriteLine("  The usual waste");
            foreach (var w in WasteFinder.Find(Places.Real()).OrderByDescending(w => w.Size))
                o.WriteLine($"  {(w.Size == 0 && w.Unreadable ? "?" : Text.Size(w.Size)),10}  {w.Display}{(w.How == CleanHow.None ? "  (report only)" : w.Blocked is { } b ? $"  ({b})" : "")}");
        }

        o.WriteLine();
        o.Flush();
        return 0;
    }
}
