namespace pxdSpaceAnalyzer.Core;

public sealed class CleanResult
{
    public long Freed;
    public long Deleted;
    /// <summary>Files in use or not allowed (skipped, never forced).</summary>
    public long Skipped;
    public string? Message;
}

/// <summary>Sizes of folders, wildcards and single files, measured without following junctions.</summary>
public static class Measure
{
    public readonly record struct Sum(long Size, long Alloc, long Files, bool Unreadable, bool Exists);

    /// <summary>A whole folder (parallel scan); <paramref name="olderThan"/> counts only files last changed before it.</summary>
    public static Sum Folder(string path, DateTime? olderThan = null)
    {
        if (!Directory.Exists(path)) return new Sum(0, 0, 0, false, false);
        if (olderThan is { } cut)
        {
            long size = 0, alloc = 0, files = 0;
            var cluster = Native.ClusterSize(path);
            var bad = false;
            Cleaner.Walk(path, (file, d) =>
            {
                if (DateTime.FromFileTime(Math.Max(0, d.ftLastWriteTime)) >= cut) return;
                var s = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
                size += s;
                alloc += Scanner.Round(s, cluster);
                files++;
            }, () => bad = true);
            return new Sum(size, alloc, files, bad, true);
        }

        var r = Scanner.Scan(path, threads: 4);
        return new Sum(r.Root.Size, r.Root.Alloc, r.Root.FileCount, r.UnreadableCount > 0, true);
    }

    /// <summary>Files matching <paramref name="pattern"/> directly in <paramref name="dir"/>.</summary>
    public static Sum Pattern(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return new Sum(0, 0, 0, false, false);
        try
        {
            long size = 0, files = 0;
            var cluster = Native.ClusterSize(dir);
            long alloc = 0;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles(pattern))
            {
                size += f.Length;
                alloc += Scanner.Round(f.Length, cluster);
                files++;
            }

            return new Sum(size, alloc, files, false, files > 0);
        }
        catch
        {
            return new Sum(0, 0, 0, true, true);
        }
    }

    /// <summary>One file, read from its folder's listing (works for hiberfil.sys and pagefile.sys, which can't be opened).</summary>
    public static Sum File(string path)
    {
        using var h = Native.FindPattern(path, out var d);
        if (h is null) return new Sum(0, 0, 0, false, false);
        var s = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
        return new Sum(s, Scanner.Round(s, Native.ClusterSize(path)), 1, false, true);
    }
}

/// <summary>
/// Deletes the usual waste. Everything goes through <see cref="Clean"/>, which only touches paths inside the item's own
/// targets, never follows a junction or folder link (it's left alone), skips files in use instead of forcing them and
/// never removes the target folder itself.
/// </summary>
public static class Cleaner
{
    /// <summary>Walks every file under <paramref name="dir"/> (not into links); <paramref name="denied"/> is called for a folder that can't be read.</summary>
    internal static void Walk(string dir, Action<string, Native.WIN32_FIND_DATA> file, Action? denied = null)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            using var h = Native.FindFirst(d, out var data, out var err);
            if (h is null)
            {
                if (err is not (2 or 3 or 18)) denied?.Invoke();
                continue;
            }

            do
            {
                var name = data.cFileName;
                if (name is "." or "..") continue;
                var full = Path.Combine(d, name);
                if ((data.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    if ((data.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) == 0) stack.Push(full);
                    continue;
                }

                file(full, data);
            } while (Native.FindNextFile(h, out data));
        }
    }

    /// <summary>
    /// Deletes what's inside <paramref name="dir"/> (not the folder itself), permanently: files last changed before
    /// <paramref name="olderThan"/> when given, then folders left empty that were made before it. Junctions and links are
    /// skipped (their targets are never touched); files in use are counted as skipped.
    /// </summary>
    public static void DeleteContents(string dir, DateTime? olderThan, CleanResult r)
    {
        if (!Directory.Exists(dir)) return;
        var cluster = Native.ClusterSize(dir);
        var folders = new List<string>();
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            using (var h = Native.FindFirst(d, out var data, out var err))
            {
                if (h is null)
                {
                    if (err is not (2 or 3 or 18)) r.Skipped++;
                    continue;
                }

                do
                {
                    var name = data.cFileName;
                    if (name is "." or "..") continue;
                    var full = Path.Combine(d, name);
                    if ((data.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        if ((data.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;   // a link: leave it and what it points to
                        if (olderThan is { } cut && DateTime.FromFileTime(Math.Max(0, data.ftCreationTime)) >= cut) { stack.Push(full); continue; }
                        stack.Push(full);
                        folders.Add(full);
                        continue;
                    }

                    if (olderThan is { } c2 && DateTime.FromFileTime(Math.Max(0, data.ftLastWriteTime)) >= c2) continue;
                    var size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    if (DeleteFile(full, data.dwFileAttributes))
                    {
                        r.Deleted++;
                        r.Freed += Scanner.Round(size, cluster);
                    }
                    else
                    {
                        r.Skipped++;
                    }
                } while (Native.FindNextFile(h, out data));
            }
        }

        // Deepest first, so a folder whose subfolders went is empty by the time it's tried.
        foreach (var f in folders.OrderByDescending(f => f.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(Native.Long(f)).Any()) Directory.Delete(Native.Long(f), recursive: false);
            }
            catch
            {
                /* in use or not empty: it stays */
            }
        }
    }

    private static bool DeleteFile(string path, int attributes)
    {
        try
        {
            var p = Native.Long(path);
            if ((attributes & 0x1) != 0) File.SetAttributes(p, FileAttributes.Normal);   // read-only
            File.Delete(p);
            return !File.Exists(p);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Files matching <paramref name="pattern"/> directly in <paramref name="dir"/>, permanently (locked ones skipped).</summary>
    public static void DeletePattern(string dir, string pattern, CleanResult r)
    {
        if (!Directory.Exists(dir)) return;
        var cluster = Native.ClusterSize(dir);
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(dir).EnumerateFiles(pattern).ToList();
        }
        catch
        {
            r.Skipped++;
            return;
        }

        foreach (var f in files)
        {
            if (f.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var size = f.Length;
            if (DeleteFile(f.FullName, (int)f.Attributes))
            {
                r.Deleted++;
                r.Freed += Scanner.Round(size, cluster);
            }
            else
            {
                r.Skipped++;
            }
        }
    }

    /// <summary>Cleans one item of the usual waste. Never throws; the result says what happened.</summary>
    public static CleanResult Clean(WasteItem item, Places places)
    {
        var r = new CleanResult();
        try
        {
            if (!item.CanClean)
            {
                r.Message = item.Blocked ?? "This one is only reported; nothing was deleted.";
                return r;
            }

            if (item.NeedsAdmin && !places.Elevated)
            {
                r.Message = "Needs administrator rights.";
                return r;
            }

            // Last check before anything goes: the browser may have been opened since the list was made.
            if (item.Process is { } proc && places.IsRunning(proc))
            {
                r.Message = $"{item.ProcessName} is open. Close it and try again.";
                return r;
            }

            switch (item.How)
            {
                case CleanHow.EmptyRecycleBin:
                    foreach (var t in item.Targets)
                    {
                        var before = places.QueryRecycleBin(t.Path)?.Size ?? 0;
                        if (places.EmptyRecycleBin(t.Path))
                        {
                            r.Freed += before;
                            r.Deleted++;
                        }
                        else
                        {
                            r.Skipped++;
                        }
                    }

                    break;

                case CleanHow.DeliveryOptimization:
                    var done = places.ClearDeliveryOptimization();
                    if (done == true)
                    {
                        r.Freed = item.Size;
                        r.Deleted = item.Files;
                    }
                    else
                    {
                        // No cmdlet (or it failed): delete the cache files themselves, skipping the ones in use.
                        foreach (var t in item.Targets) DeleteContents(t.Path, null, r);
                    }

                    break;

                case CleanHow.RecycleBin:
                    foreach (var t in item.Targets)
                    {
                        // One file at a time (never a whole folder: the shell could step into a link inside it).
                        var paths = new List<string>();
                        if (t.Kind == TargetKind.File && File.Exists(t.Path)) paths.Add(t.Path);
                        else if (t.Kind == TargetKind.Pattern && Directory.Exists(t.Path)) paths.AddRange(Directory.GetFiles(t.Path, t.Pattern!));
                        else if (t.Kind == TargetKind.Contents && Directory.Exists(t.Path)) Walk(t.Path, (f, _) => paths.Add(f));
                        foreach (var p in paths)
                        {
                            if (t.Kind != TargetKind.File && !Places.Inside(p, t.Path)) continue;
                            var info = new FileInfo(p);
                            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                            var size = Scanner.Round(info.Length, Native.ClusterSize(p));
                            if (places.Recycle(p))
                            {
                                r.Deleted++;
                                r.Freed += size;
                            }
                            else
                            {
                                r.Skipped++;
                            }
                        }
                    }

                    break;

                case CleanHow.Permanent:
                    foreach (var t in item.Targets)
                    {
                        switch (t.Kind)
                        {
                            case TargetKind.Contents:
                                DeleteContents(t.Path, item.OlderThanDays > 0 ? DateTime.Now.AddDays(-item.OlderThanDays) : null, r);
                                break;
                            case TargetKind.Pattern:
                                DeletePattern(t.Path, t.Pattern!, r);
                                break;
                            case TargetKind.File:
                                if (File.Exists(t.Path))
                                {
                                    var size = Scanner.Round(new FileInfo(t.Path).Length, Native.ClusterSize(t.Path));
                                    if (DeleteFile(t.Path, (int)File.GetAttributes(t.Path)))
                                    {
                                        r.Deleted++;
                                        r.Freed += size;
                                    }
                                    else r.Skipped++;
                                }

                                break;
                        }
                    }

                    break;
            }

            r.Message = r.Skipped > 0
                ? $"Freed {Text.Size(r.Freed)}. {Text.Plural(r.Skipped, "item")} in use or not allowed {(r.Skipped == 1 ? "was" : "were")} skipped."
                : $"Freed {Text.Size(r.Freed)}.";
        }
        catch (Exception ex)
        {
            r.Message = "Stopped: " + ex.Message;
        }

        return r;
    }
}
