using System.Collections.Concurrent;
using System.Diagnostics;

namespace pxdSpaceAnalyzer.Core;

[Flags]
public enum NodeFlags
{
    None = 0,
    /// <summary>A junction or symbolic link to a folder: shown, never followed.</summary>
    Link = 1,
    /// <summary>Windows said no (access denied or another error).</summary>
    Unreadable = 2,
}

/// <summary>A file kept by name in its folder (the others are only counted, see <see cref="DirNode.SmallCount"/>).</summary>
public readonly record struct FileEntry(string Name, long Size, long Alloc, long LastWriteFt)
{
    public DateTime LastWrite => LastWriteFt > 0 ? DateTime.FromFileTime(LastWriteFt) : DateTime.MinValue;
}

/// <summary>A folder and, after the scan, the totals of everything below it.</summary>
public sealed class DirNode
{
    public string Name { get; }
    public DirNode? Parent { get; }
    internal string? RootPath;

    public List<DirNode> Dirs { get; } = [];
    /// <summary>The bigger files directly in this folder, biggest first.</summary>
    public FileEntry[] Files { get; internal set; } = [];

    /// <summary>Files directly here that aren't kept by name (smaller ones in a folder with many files).</summary>
    public int SmallCount { get; internal set; }
    public long SmallSize { get; internal set; }
    public long SmallAlloc { get; internal set; }

    public int OwnFiles { get; internal set; }
    public long OwnSize { get; internal set; }
    public long OwnAlloc { get; internal set; }
    internal long OwnLastWrite;

    /// <summary>Totals for this folder and everything inside it.</summary>
    public long Size { get; internal set; }
    public long Alloc { get; internal set; }
    public long FileCount { get; internal set; }
    public long DirCount { get; internal set; }
    public long LastWriteFt { get; internal set; }
    public DateTime LastWrite => LastWriteFt > 0 ? DateTime.FromFileTime(LastWriteFt) : DateTime.MinValue;

    public NodeFlags Flags { get; internal set; }
    public bool IsLink => Flags.HasFlag(NodeFlags.Link);
    public bool Unreadable => Flags.HasFlag(NodeFlags.Unreadable);

    public DirNode(string name, DirNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    public static DirNode Root(string path) => new(path, null) { RootPath = path };

    public string FullPath
    {
        get
        {
            if (Parent is null) return RootPath ?? Name;
            var parts = new Stack<string>();
            DirNode? n = this;
            while (n!.Parent is not null)
            {
                parts.Push(n.Name);
                n = n.Parent;
            }

            var root = n.RootPath ?? n.Name;
            return Path.Join(root, string.Join('\\', parts));
        }
    }

    public int Depth
    {
        get
        {
            var d = 0;
            for (var p = Parent; p is not null; p = p.Parent) d++;
            return d;
        }
    }

    /// <summary>The folder at <paramref name="path"/> inside this tree (case-insensitive), or null.</summary>
    public DirNode? Find(string path)
    {
        var mine = FullPath.TrimEnd('\\');
        var want = path.TrimEnd('\\');
        if (want.Equals(mine, StringComparison.OrdinalIgnoreCase)) return this;
        if (!want.StartsWith(mine + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        var n = this;
        foreach (var part in want[(mine.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            n = n.Dirs.FirstOrDefault(d => d.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (n is null) return null;
        }

        return n;
    }
}

public sealed record FileHit(string Path, long Size, long Alloc, DateTime LastWrite)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public sealed class ExtStat
{
    public string Ext { get; init; } = "";
    public long Count { get; set; }
    public long Size { get; set; }
    public long Alloc { get; set; }
}

/// <summary>Live counters while a scan runs (read from any thread).</summary>
public sealed class ScanProgress
{
    internal long FilesN, DirsN, BytesN;
    internal volatile string CurrentPath = "";
    public long Files => Interlocked.Read(ref FilesN);
    public long Dirs => Interlocked.Read(ref DirsN);
    public long Bytes => Interlocked.Read(ref BytesN);
    public string Current => CurrentPath;
}

public sealed class ScanResult
{
    public required DirNode Root { get; init; }
    public required List<FileHit> TopFiles { get; init; }
    public required List<ExtStat> Extensions { get; init; }
    /// <summary>Folders Windows wouldn't let us read.</summary>
    public long UnreadableCount { get; init; }
    public List<string> UnreadableSample { get; init; } = [];
    /// <summary>Junctions and folder links that were not followed.</summary>
    public long LinksSkipped { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Cancelled { get; init; }
    public long ClusterSize { get; init; }
}

/// <summary>
/// Lists a folder tree fast: several threads share a work stack of folders, each read with FindFirstFileExW
/// (basic info, large fetch). Sizes are counted twice: the file's size and the space it takes on disk (rounded up to
/// the cluster size; compressed, sparse and cloud files ask Windows for their real size). Junctions and folder links
/// are never followed. Folders that can't be read are counted and left out quietly.
/// </summary>
public static class Scanner
{
    public const int TopFileCount = 100;
    /// <summary>A folder with up to this many files keeps them all by name; a bigger one keeps the biggest and files ≥ <see cref="KeepAlways"/>.</summary>
    public const int KeepPerFolder = 64;
    public const long KeepAlways = 16L << 20;

    private const int ProbeAttrs = Native.FILE_ATTRIBUTE_SPARSE_FILE | Native.FILE_ATTRIBUTE_COMPRESSED | Native.FILE_ATTRIBUTE_OFFLINE
                                   | Native.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Native.FILE_ATTRIBUTE_RECALL_ON_OPEN | Native.FILE_ATTRIBUTE_REPARSE_POINT;

    private sealed class Worker
    {
        public readonly Dictionary<string, ExtStat> Ext = new(StringComparer.Ordinal);
        public readonly PriorityQueue<FileHit, long> Top = new();
        public readonly List<FileEntry> Buffer = [];
    }

    public static ScanResult Scan(string path, ScanProgress? progress = null, CancellationToken cancel = default, int? threads = null)
    {
        var watch = Stopwatch.StartNew();
        progress ??= new ScanProgress();
        var full = Path.GetFullPath(path);
        if (full.Length > 3) full = full.TrimEnd('\\');
        var root = DirNode.Root(full);
        var cluster = Native.ClusterSize(full);
        long unreadable = 0, links = 0;
        var sample = new ConcurrentQueue<string>();

        var work = new BlockingCollection<(DirNode Node, string Path)>(new ConcurrentStack<(DirNode, string)>());
        long pending = 1;
        work.Add((root, full));
        var count = threads ?? Math.Clamp(Environment.ProcessorCount, 4, 16);
        var workers = Enumerable.Range(0, count).Select(_ => new Worker()).ToArray();

        void Run(Worker me)
        {
            try
            {
                foreach (var (node, dir) in work.GetConsumingEnumerable(cancel))
                {
                    try
                    {
                        ReadFolder(me, node, dir, cluster, progress, ref unreadable, ref links, sample, child =>
                        {
                            Interlocked.Increment(ref pending);
                            work.Add(child);
                        });
                    }
                    catch
                    {
                        /* a folder that vanished mid-scan */
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref pending) == 0) work.CompleteAdding();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                /* stopped */
            }
        }

        var ts = workers.Select(w => new Thread(() => Run(w)) { IsBackground = true, Name = "scan" }).ToArray();
        foreach (var t in ts) t.Start();
        foreach (var t in ts) t.Join();

        Totals(root);

        // Merge the workers' extension counts and biggest files.
        var ext = new Dictionary<string, ExtStat>(StringComparer.Ordinal);
        var top = new List<FileHit>();
        foreach (var w in workers)
        {
            foreach (var (k, v) in w.Ext)
            {
                if (!ext.TryGetValue(k, out var e)) ext[k] = e = new ExtStat { Ext = k };
                e.Count += v.Count;
                e.Size += v.Size;
                e.Alloc += v.Alloc;
            }

            while (w.Top.TryDequeue(out var hit, out _)) top.Add(hit);
        }

        return new ScanResult
        {
            Root = root,
            TopFiles = top.OrderByDescending(h => h.Alloc).ThenByDescending(h => h.Size).Take(TopFileCount).ToList(),
            Extensions = ext.Values.OrderByDescending(e => e.Alloc).ToList(),
            UnreadableCount = unreadable,
            UnreadableSample = sample.Take(50).ToList(),
            LinksSkipped = links,
            Elapsed = watch.Elapsed,
            Cancelled = cancel.IsCancellationRequested,
            ClusterSize = cluster,
        };
    }

    private static void ReadFolder(Worker me, DirNode node, string dir, long cluster, ScanProgress progress, ref long unreadable, ref long links,
        ConcurrentQueue<string> sample, Action<(DirNode, string)> add)
    {
        progress.CurrentPath = dir;
        using var h = Native.FindFirst(dir, out var d, out var error);
        if (h is null)
        {
            if (error is 2 or 3 or 18) return;   // gone, or simply empty
            node.Flags |= NodeFlags.Unreadable;
            Interlocked.Increment(ref unreadable);
            if (sample.Count < 50) sample.Enqueue(dir);
            return;
        }

        var files = me.Buffer;
        files.Clear();
        long ownSize = 0, ownAlloc = 0, last = 0;
        var children = new List<DirNode>();
        do
        {
            var name = d.cFileName;
            if ((d.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                if (name is "." or "..") continue;
                var child = new DirNode(name, node);
                children.Add(child);
                Interlocked.Increment(ref progress.DirsN);
                if ((d.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                {
                    child.Flags |= NodeFlags.Link;
                    Interlocked.Increment(ref links);
                    continue;
                }

                add((child, dir.EndsWith('\\') ? dir + name : dir + "\\" + name));
                continue;
            }

            var size = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
            long alloc;
            if ((d.dwFileAttributes & ProbeAttrs) != 0)
            {
                var real = Native.CompressedSize(dir.EndsWith('\\') ? dir + name : dir + "\\" + name) ?? size;
                alloc = Round(real, cluster);
            }
            else
            {
                alloc = Round(size, cluster);
            }

            ownSize += size;
            ownAlloc += alloc;
            if (d.ftLastWriteTime > last) last = d.ftLastWriteTime;
            files.Add(new FileEntry(name, size, alloc, d.ftLastWriteTime));

            var dot = name.LastIndexOf('.');
            var ext = dot > 0 && dot < name.Length - 1 && name.Length - dot <= 16 ? name[dot..].ToLowerInvariant() : "";
            if (!me.Ext.TryGetValue(ext, out var es)) me.Ext[ext] = es = new ExtStat { Ext = ext };
            es.Count++;
            es.Size += size;
            es.Alloc += alloc;

            if (me.Top.Count < TopFileCount)
                me.Top.Enqueue(new FileHit(Join(dir, name), size, alloc, DateTime.FromFileTime(Math.Max(0, d.ftLastWriteTime))), alloc);
            else if (me.Top.TryPeek(out _, out var min) && alloc > min)
                me.Top.EnqueueDequeue(new FileHit(Join(dir, name), size, alloc, DateTime.FromFileTime(Math.Max(0, d.ftLastWriteTime))), alloc);
        } while (Native.FindNextFile(h, out d));

        Interlocked.Add(ref progress.FilesN, files.Count);
        Interlocked.Add(ref progress.BytesN, ownAlloc);
        node.OwnFiles = files.Count;
        node.OwnSize = ownSize;
        node.OwnAlloc = ownAlloc;
        node.OwnLastWrite = last;

        // Keep the files a person would look for by name; count the rest.
        files.Sort((a, b) => b.Alloc.CompareTo(a.Alloc));
        var keep = files.Count <= KeepPerFolder ? files.Count : Math.Max(KeepPerFolder, files.FindIndex(f => f.Alloc < KeepAlways) is var i and >= 0 ? i : files.Count);
        node.Files = files.GetRange(0, keep).ToArray();
        for (var k = keep; k < files.Count; k++)
        {
            node.SmallCount++;
            node.SmallSize += files[k].Size;
            node.SmallAlloc += files[k].Alloc;
        }

        lock (node) node.Dirs.AddRange(children);
    }

    private static string Join(string dir, string name) => dir.EndsWith('\\') ? dir + name : dir + "\\" + name;

    public static long Round(long size, long cluster) => size <= 0 ? 0 : (size + cluster - 1) / cluster * cluster;

    /// <summary>Adds up every folder from the bottom (no recursion: trees can be very deep) and sorts each folder's subfolders biggest first.</summary>
    internal static void Totals(DirNode root)
    {
        var order = new List<DirNode>();
        var stack = new Stack<DirNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            order.Add(n);
            foreach (var c in n.Dirs) stack.Push(c);
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            long size = n.OwnSize, alloc = n.OwnAlloc, files = n.OwnFiles, dirs = n.Dirs.Count, last = n.OwnLastWrite;
            foreach (var c in n.Dirs)
            {
                size += c.Size;
                alloc += c.Alloc;
                files += c.FileCount;
                dirs += c.DirCount;
                if (c.LastWriteFt > last) last = c.LastWriteFt;
            }

            n.Size = size;
            n.Alloc = alloc;
            n.FileCount = files;
            n.DirCount = dirs;
            n.LastWriteFt = last;
            n.Dirs.Sort((a, b) => b.Alloc.CompareTo(a.Alloc));
        }
    }

    /// <summary>Removes a folder from the tree after it was deleted, and takes its sizes off every folder above it.</summary>
    public static void Remove(DirNode node)
    {
        if (node.Parent is not { } parent) return;
        parent.Dirs.Remove(node);
        for (var p = parent; p is not null; p = p.Parent)
        {
            p.Size -= node.Size;
            p.Alloc -= node.Alloc;
            p.FileCount -= node.FileCount;
            p.DirCount -= node.DirCount + 1;
        }
    }

    /// <summary>Removes a file kept by name (after it was deleted) and takes its size off every folder above it.</summary>
    public static void RemoveFile(DirNode folder, FileEntry file)
    {
        folder.Files = folder.Files.Where(f => f != file).ToArray();
        folder.OwnFiles--;
        folder.OwnSize -= file.Size;
        folder.OwnAlloc -= file.Alloc;
        for (var p = folder; p is not null; p = p.Parent)
        {
            p.Size -= file.Size;
            p.Alloc -= file.Alloc;
            p.FileCount--;
        }
    }
}
