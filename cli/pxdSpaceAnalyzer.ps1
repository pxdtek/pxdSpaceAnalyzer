#Requires -Version 5.1
<#
.SYNOPSIS
    pxd Space Analyzer (CLI) - what's filling a drive, right in the console.

.DESCRIPTION
    The console edition of pxd Space Analyzer. Nothing is installed and nothing is saved. Run it straight from the web:

        irm pxdtek.com/sa | iex

    (pxdtek.com serves the latest copy of
        https://raw.githubusercontent.com/pxdtek/pxdSpaceAnalyzer/main/cli/pxdSpaceAnalyzer.ps1)

    It shows each fixed drive with a used/free bar, scans a drive or folder fast (several threads, FindFirstFileEx;
    junctions and folder links are never followed, folders it can't read are counted quietly), then lets you walk the
    biggest folders (type a number to open one, B to go back), see the biggest files and the space by file type, and
    lists the usual waste (temp files, update caches, the Recycle Bin, hiberfil.sys, crash dumps, browser caches...)
    with a plain-English note each. The clean-up menu only offers the safe ones and asks before each.

    With parameters through the web:
        & ([scriptblock]::Create((irm pxdtek.com/sa))) -Report -Path D:\Games

.PARAMETER Path
    A drive or folder to scan straight away (with -Report: print its biggest folders, files and file types).
.PARAMETER Report
    Non-interactive: print the drives (and -Path's scan, and the usual waste with -Waste), then return.
.PARAMETER Waste
    With -Report: also list the usual waste.
.PARAMETER Top
    How many rows each list shows (default 20).
#>
[CmdletBinding()]
param(
    [string]$Path = '',
    [switch]$Report,
    [switch]$Waste,
    [int]$Top = 20
)

$script:PxsaVersion = '1.0.0'
$script:PxsaUrl = 'pxdtek.com/sa'
# In-memory mode (set by the pxd Toolkit console while it runs this script): nothing is saved either way; the mode is
# shown and carried over to an elevated window (A).
$script:PxsaInMemory = $env:PXD_INMEMORY -eq '1'
$script:PxsaEof = $false
# Test hook: PXDSA_TEST_ROOT maps Windows, ProgramData, Users and the drive's Recycle Bin into a made-up tree, so the
# clean-up can be tried without touching anything real. PXDSA_TEST_RUNNING lists "open" browsers (msedge,chrome,firefox).
$script:PxsaTestRoot = $env:PXDSA_TEST_ROOT
$script:PxsaTestRunning = @(("$env:PXDSA_TEST_RUNNING" -split '[,; ]') | Where-Object { $_ })
$script:PxsaFast = $false
$script:PxsaScan = $null

# ======================================================================================
#  Fast scanner (C#, compiled once per session with Add-Type; a plain PowerShell walk if that isn't allowed)
# ======================================================================================

$script:PxsaSource = @'
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PxdSa100
{
    public class Node
    {
        public string Name; public string FullPath; public Node Parent;
        public List<Node> Dirs = new List<Node>();
        public List<Item> Files = new List<Item>();
        public long Size, Alloc, FileCount, DirCount, LastWrite;
        public long OwnSize, OwnAlloc, OwnLastWrite, SmallSize, SmallAlloc;
        public int OwnFiles, SmallCount;
        public bool Link, Unreadable;
    }

    public class Item { public string Name; public string Path; public long Size, Alloc, LastWrite; }

    public class ExtStat { public string Ext; public long Count, Size, Alloc; }

    public class Result
    {
        public Node Root; public List<Item> Top; public List<ExtStat> Exts;
        public long Unreadable, Links; public double Seconds; public bool Cancelled;
    }

    public class Progress { public long Files, Dirs, Bytes; }

    public static class Scanner
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        struct FindData
        {
            public int Attr; public long Created; public long Accessed; public long Written;
            public uint SizeHigh; public uint SizeLow; public uint R0; public uint R1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string Alt;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileExW(string name, int level, out FindData data, int op, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFileW(IntPtr h, out FindData data);
        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetCompressedFileSizeW(string name, out uint high);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetDiskFreeSpaceW(string root, out uint spc, out uint bps, out uint free, out uint total);

        const int Dir = 0x10, Reparse = 0x400, Probe = 0x200 | 0x800 | 0x1000 | 0x40000 | 0x400000 | 0x400;
        public const int KeepPerFolder = 64;
        public const long KeepAlways = 16L << 20;

        public static string Long(string p)
        {
            if (p.StartsWith(@"\\?\")) return p;
            if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
            return @"\\?\" + p;
        }

        public static long ClusterSize(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                uint spc, bps, f, t;
                if (!string.IsNullOrEmpty(root) && GetDiskFreeSpaceW(root.EndsWith("\\") ? root : root + "\\", out spc, out bps, out f, out t) && spc > 0 && bps > 0) return (long)spc * bps;
            }
            catch { }
            return 4096;
        }

        public static long Round(long size, long cluster) { return size <= 0 ? 0 : (size + cluster - 1) / cluster * cluster; }

        static string Join(string dir, string name) { return dir.EndsWith("\\") ? dir + name : dir + "\\" + name; }

        class Worker
        {
            public Dictionary<string, ExtStat> Ext = new Dictionary<string, ExtStat>();
            public List<Item> Top = new List<Item>();
            public long Min = 0;
        }

        public static Result Scan(string path, int threads, Progress progress, CancellationToken cancel)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string full = Path.GetFullPath(path);
            if (full.Length > 3) full = full.TrimEnd('\\');
            var root = new Node { Name = full, FullPath = full };
            long cluster = ClusterSize(full);
            long unreadable = 0, links = 0, pending = 1;
            var work = new BlockingCollection<Node>(new ConcurrentStack<Node>());
            work.Add(root);
            if (threads < 1) threads = Math.Max(4, Math.Min(16, Environment.ProcessorCount));
            var workers = new Worker[threads];
            var ts = new Thread[threads];
            for (int i = 0; i < threads; i++)
            {
                var me = workers[i] = new Worker();
                ts[i] = new Thread(() =>
                {
                    try
                    {
                        foreach (var node in work.GetConsumingEnumerable(cancel))
                        {
                            try { Read(me, node, cluster, progress, ref unreadable, ref links, work, ref pending); }
                            catch { }
                            finally { if (Interlocked.Decrement(ref pending) == 0) work.CompleteAdding(); }
                        }
                    }
                    catch (OperationCanceledException) { }
                });
                ts[i].IsBackground = true;
                ts[i].Start();
            }
            foreach (var t in ts) t.Join();
            Totals(root);

            var ext = new Dictionary<string, ExtStat>();
            var top = new List<Item>();
            foreach (var w in workers)
            {
                foreach (var kv in w.Ext)
                {
                    ExtStat e;
                    if (!ext.TryGetValue(kv.Key, out e)) { e = new ExtStat { Ext = kv.Key }; ext[kv.Key] = e; }
                    e.Count += kv.Value.Count; e.Size += kv.Value.Size; e.Alloc += kv.Value.Alloc;
                }
                top.AddRange(w.Top);
            }
            top.Sort((a, b) => b.Alloc.CompareTo(a.Alloc));
            if (top.Count > 100) top.RemoveRange(100, top.Count - 100);
            var exts = new List<ExtStat>(ext.Values);
            exts.Sort((a, b) => b.Alloc.CompareTo(a.Alloc));
            return new Result { Root = root, Top = top, Exts = exts, Unreadable = unreadable, Links = links, Seconds = watch.Elapsed.TotalSeconds, Cancelled = cancel.IsCancellationRequested };
        }

        static void Read(Worker me, Node node, long cluster, Progress progress, ref long unreadable, ref long links, BlockingCollection<Node> work, ref long pending)
        {
            string dir = node.FullPath;
            FindData d;
            IntPtr h = FindFirstFileExW(Long(dir.TrimEnd('\\') + "\\*"), 1, out d, 0, IntPtr.Zero, 2);
            if (h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 2 || err == 3 || err == 18) return;
                node.Unreadable = true;
                Interlocked.Increment(ref unreadable);
                return;
            }
            var files = new List<Item>();
            var kids = new List<Node>();
            try
            {
                do
                {
                    string name = d.Name;
                    if ((d.Attr & Dir) != 0)
                    {
                        if (name == "." || name == "..") continue;
                        var child = new Node { Name = name, FullPath = Join(dir, name), Parent = node };
                        kids.Add(child);
                        Interlocked.Increment(ref progress.Dirs);
                        if ((d.Attr & Reparse) != 0) { child.Link = true; Interlocked.Increment(ref links); continue; }
                        Interlocked.Increment(ref pending);
                        work.Add(child);
                        continue;
                    }
                    long size = ((long)d.SizeHigh << 32) | d.SizeLow;
                    long alloc;
                    if ((d.Attr & Probe) != 0)
                    {
                        uint hi; uint lo = GetCompressedFileSizeW(Long(Join(dir, name)), out hi);
                        long real = (lo == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0) ? size : (((long)hi << 32) | lo);
                        alloc = Round(real, cluster);
                    }
                    else alloc = Round(size, cluster);
                    node.OwnSize += size; node.OwnAlloc += alloc;
                    if (d.Written > node.OwnLastWrite) node.OwnLastWrite = d.Written;
                    files.Add(new Item { Name = name, Size = size, Alloc = alloc, LastWrite = d.Written });
                    int dot = name.LastIndexOf('.');
                    string ext = dot > 0 && dot < name.Length - 1 && name.Length - dot <= 16 ? name.Substring(dot).ToLowerInvariant() : "";
                    ExtStat es;
                    if (!me.Ext.TryGetValue(ext, out es)) { es = new ExtStat { Ext = ext }; me.Ext[ext] = es; }
                    es.Count++; es.Size += size; es.Alloc += alloc;
                    if (me.Top.Count < 100 || alloc > me.Min)
                    {
                        me.Top.Add(new Item { Name = name, Path = Join(dir, name), Size = size, Alloc = alloc, LastWrite = d.Written });
                        if (me.Top.Count > 400)
                        {
                            me.Top.Sort((a, b) => b.Alloc.CompareTo(a.Alloc));
                            me.Top.RemoveRange(100, me.Top.Count - 100);
                            me.Min = me.Top[99].Alloc;
                        }
                    }
                } while (FindNextFileW(h, out d));
            }
            finally { FindClose(h); }

            Interlocked.Add(ref progress.Files, files.Count);
            Interlocked.Add(ref progress.Bytes, node.OwnAlloc);
            node.OwnFiles = files.Count;
            files.Sort((a, b) => b.Alloc.CompareTo(a.Alloc));
            int keep = files.Count;
            if (files.Count > KeepPerFolder)
            {
                int first = files.FindIndex(f => f.Alloc < KeepAlways);
                keep = Math.Max(KeepPerFolder, first < 0 ? files.Count : first);
            }
            for (int k = 0; k < files.Count; k++)
            {
                if (k < keep) { files[k].Path = Join(dir, files[k].Name); node.Files.Add(files[k]); }
                else { node.SmallCount++; node.SmallSize += files[k].Size; node.SmallAlloc += files[k].Alloc; }
            }
            node.Dirs.AddRange(kids);
        }

        static void Totals(Node root)
        {
            var order = new List<Node>();
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0) { var n = stack.Pop(); order.Add(n); foreach (var c in n.Dirs) stack.Push(c); }
            for (int i = order.Count - 1; i >= 0; i--)
            {
                var n = order[i];
                long size = n.OwnSize, alloc = n.OwnAlloc, files = n.OwnFiles, dirs = n.Dirs.Count, last = n.OwnLastWrite;
                foreach (var c in n.Dirs) { size += c.Size; alloc += c.Alloc; files += c.FileCount; dirs += c.DirCount; if (c.LastWrite > last) last = c.LastWrite; }
                n.Size = size; n.Alloc = alloc; n.FileCount = files; n.DirCount = dirs; n.LastWrite = last;
                n.Dirs.Sort((a, b) => b.Alloc.CompareTo(a.Alloc));
            }
        }
    }

    public static class Shell
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileOp
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
            public int fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperationW(ref FileOp op);

        // To the Recycle Bin, no windows of any kind (no confirm, no progress, no error box).
        public static bool Recycle(string path)
        {
            var op = new FileOp { wFunc = 3, pFrom = path + "\0\0", fFlags = (ushort)(0x40 | 0x10 | 0x4 | 0x400 | 0x200) };
            int r = SHFileOperationW(ref op);
            return r == 0 && op.fAnyOperationsAborted == 0 && !File.Exists(path) && !Directory.Exists(path);
        }
    }
}
'@

function Initialize-PxsaFast {
    if ($env:PXDSA_SLOW -eq '1') { $script:PxsaFast = $false; return }   # test hook: the plain PowerShell walk
    if ('PxdSa100.Scanner' -as [type]) { $script:PxsaFast = $true; return }
    try {
        Add-Type -TypeDefinition $script:PxsaSource -Language CSharp -ErrorAction Stop -WarningAction SilentlyContinue
        $script:PxsaFast = $true
    } catch { $script:PxsaFast = $false }
}

# ======================================================================================
#  Helpers
# ======================================================================================

function Test-PxsaAdmin {
    try { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch { $false }
}

function Test-PxsaSystem { try { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18' } catch { $false } }

function Get-PxsaSize([double]$bytes) {
    if ($bytes -lt 0) { return '?' }
    if ($bytes -lt 1024) { return ('{0:0} B' -f $bytes) }
    $units = 'KB', 'MB', 'GB', 'TB', 'PB'; $i = -1
    while ($bytes -ge 1024 -and $i -lt $units.Count - 1) { $bytes /= 1024; $i++ }
    $ci = [Globalization.CultureInfo]::InvariantCulture
    if ($bytes -ge 100) { return $bytes.ToString('0', $ci) + ' ' + $units[$i] }
    if ($bytes -ge 10) { return $bytes.ToString('0.0', $ci) + ' ' + $units[$i] }
    $bytes.ToString('0.00', $ci) + ' ' + $units[$i]
}

function Get-PxsaPct([double]$f) {
    if ($f -ge 0.995) { return '100%' }
    if ($f -gt 0 -and $f -lt 0.001) { return '<0.1%' }
    $ci = [Globalization.CultureInfo]::InvariantCulture
    if ($f -lt 0.1) { return ($f * 100).ToString('0.0', $ci) + '%' }
    ($f * 100).ToString('0', $ci) + '%'
}

function Get-PxsaRound([long]$size) { if ($size -le 0) { return [long]0 }; [long]([Math]::Ceiling($size / 4096.0) * 4096) }

# A bar of $Width cells filled to $Fraction (block characters made at run time so this file stays ASCII).
function Write-PxsaBar([double]$Fraction, [int]$Width = 24, [string]$Color = 'Green') {
    if ($Fraction -lt 0 -or [double]::IsNaN($Fraction)) { $Fraction = 0 }
    if ($Fraction -gt 1) { $Fraction = 1 }
    $n = [int][Math]::Round($Fraction * $Width)
    if ($Fraction -gt 0 -and $n -eq 0) { $n = 1 }
    Write-Host ([string]([char]0x2588) * $n) -NoNewline -ForegroundColor $Color
    Write-Host ([string]([char]0x2591) * ($Width - $n)) -NoNewline -ForegroundColor DarkGray
}

function Get-PxsaWidth { $w = 100; try { $w = $Host.UI.RawUI.WindowSize.Width } catch { }; if ($w -lt 60) { $w = 100 }; $w }

function Get-PxsaCut([string]$s, [int]$n) { if (-not $s) { return '' }; if ($n -lt 4) { $n = 4 }; if ($s.Length -le $n) { return $s }; $s.Substring(0, $n - 1) + '~' }

function Write-PxsaHead([string]$Title) {
    Write-Host ''
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host ('  ' + ('-' * [Math]::Min(76, $Title.Length + 2))) -ForegroundColor DarkGray
}

function Read-PxsaKey([string]$Prompt) {
    $k = Read-Host $Prompt
    if ($null -eq $k) { $script:PxsaEof = $true; return 'q' }   # piped input ran out
    $k.Trim()
}

function Test-PxsaYes([string]$Prompt) { (Read-PxsaKey $Prompt) -match '^(?i)y(es)?$' }

function Restart-PxsaElevated {
    $self = $PSCommandPath
    $exe = (Get-Process -Id $PID).Path
    $set = if ($script:PxsaInMemory) { "`$env:PXD_INMEMORY='1'; " } else { '' }   # the elevated window doesn't inherit this environment
    $arg = if ($self) { "-NoExit -ExecutionPolicy Bypass -Command `"$set& '$self'`"" }
    else { "-NoExit -ExecutionPolicy Bypass -Command `"$set& ([scriptblock]::Create((irm $($script:PxsaUrl))))`"" }
    try { Start-Process -FilePath $exe -ArgumentList $arg -Verb RunAs -ErrorAction Stop; $true } catch { Write-Host '  Not elevated (the prompt was cancelled).' -ForegroundColor Yellow; $false }
}

# ======================================================================================
#  Drives
# ======================================================================================

function Get-PxsaDrives {
    $list = @()
    foreach ($d in [IO.DriveInfo]::GetDrives()) {
        try {
            if ($d.DriveType -ne 'Fixed' -or -not $d.IsReady) { continue }
            $list += [pscustomobject]@{ Root = $d.RootDirectory.FullName; Label = $d.VolumeLabel; Format = $d.DriveFormat; Total = [long]$d.TotalSize; Free = [long]$d.TotalFreeSpace }
        } catch { }
    }
    $list
}

function Show-PxsaDrives($Drives) {
    Write-PxsaHead 'Drives'
    $i = 0
    foreach ($d in $Drives) {
        $i++
        $used = if ($d.Total -gt 0) { ($d.Total - $d.Free) / $d.Total } else { 0 }
        $color = if ($used -gt 0.9) { 'Red' } elseif ($used -gt 0.75) { 'Yellow' } else { 'Green' }
        $name = $d.Root.TrimEnd('\')
        if ($d.Label) { $name += " $($d.Label)" }
        Write-Host ('  {0,2}  ' -f $i) -NoNewline -ForegroundColor Cyan
        Write-Host ('{0,-16} ' -f (Get-PxsaCut $name 16)) -NoNewline
        Write-PxsaBar $used 28 $color
        Write-Host ('  {0,9} free of {1,-9} {2,5} full' -f (Get-PxsaSize $d.Free), (Get-PxsaSize $d.Total), (Get-PxsaPct $used)) -ForegroundColor Gray
    }
    if (-not $Drives) { Write-Host '  No fixed drives found.' -ForegroundColor DarkGray }
}

# ======================================================================================
#  Scanning
# ======================================================================================

# Plain PowerShell walk (when Add-Type isn't allowed): the same shape of result, slower.
function Invoke-PxsaSlowScan([string]$Folder) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $full = [IO.Path]::GetFullPath($Folder); if ($full.Length -gt 3) { $full = $full.TrimEnd('\') }
    $new = { param($name, $path, $parent) [pscustomobject]@{ Name = $name; FullPath = $path; Parent = $parent; Dirs = (New-Object Collections.ArrayList); Files = (New-Object Collections.ArrayList)
            Size = [long]0; Alloc = [long]0; FileCount = [long]0; DirCount = [long]0; LastWrite = [long]0; OwnSize = [long]0; OwnAlloc = [long]0; OwnFiles = 0; OwnLastWrite = [long]0
            SmallCount = 0; SmallSize = [long]0; SmallAlloc = [long]0; Link = $false; Unreadable = $false } }
    $root = & $new $full $full $null
    $all = New-Object Collections.ArrayList
    $queue = New-Object Collections.Queue
    $queue.Enqueue($root)
    $exts = @{}; $top = New-Object Collections.ArrayList
    $unreadable = 0; $links = 0
    while ($queue.Count -gt 0) {
        $n = $queue.Dequeue(); [void]$all.Add($n)
        $files = New-Object Collections.ArrayList
        try {
            foreach ($e in ([IO.DirectoryInfo]$n.FullPath).EnumerateFileSystemInfos()) {
                if ($e.Attributes -band [IO.FileAttributes]::Directory) {
                    $c = & $new $e.Name $e.FullName $n
                    [void]$n.Dirs.Add($c)
                    if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) { $c.Link = $true; $links++ } else { $queue.Enqueue($c) }
                } else {
                    $s = [long]$e.Length; $a = Get-PxsaRound $s; $w = $e.LastWriteTime.ToFileTime()
                    $n.OwnSize += $s; $n.OwnAlloc += $a; if ($w -gt $n.OwnLastWrite) { $n.OwnLastWrite = $w }
                    $it = [pscustomobject]@{ Name = $e.Name; Path = $e.FullName; Size = $s; Alloc = $a; LastWrite = $w }
                    [void]$files.Add($it)
                    $x = $e.Extension.ToLowerInvariant()
                    if (-not $exts.ContainsKey($x)) { $exts[$x] = [pscustomobject]@{ Ext = $x; Count = [long]0; Size = [long]0; Alloc = [long]0 } }
                    $exts[$x].Count++; $exts[$x].Size += $s; $exts[$x].Alloc += $a
                    [void]$top.Add($it)
                    if ($top.Count -gt 2000) { $keep = @($top | Sort-Object Alloc -Descending | Select-Object -First 100); $top = New-Object Collections.ArrayList; [void]$top.AddRange($keep) }
                }
            }
        } catch { $n.Unreadable = $true; $unreadable++ }
        $n.OwnFiles = $files.Count
        $sorted = @($files | Sort-Object Alloc -Descending)
        $i = 0
        foreach ($f in $sorted) {
            if ($i -lt 64 -or $f.Alloc -ge 16MB) { [void]$n.Files.Add($f) } else { $n.SmallCount++; $n.SmallSize += $f.Size; $n.SmallAlloc += $f.Alloc }
            $i++
        }
    }
    for ($i = $all.Count - 1; $i -ge 0; $i--) {
        $n = $all[$i]
        $n.Size = $n.OwnSize; $n.Alloc = $n.OwnAlloc; $n.FileCount = $n.OwnFiles; $n.DirCount = $n.Dirs.Count; $n.LastWrite = $n.OwnLastWrite
        foreach ($c in $n.Dirs) { $n.Size += $c.Size; $n.Alloc += $c.Alloc; $n.FileCount += $c.FileCount; $n.DirCount += $c.DirCount; if ($c.LastWrite -gt $n.LastWrite) { $n.LastWrite = $c.LastWrite } }
        $sortedDirs = @($n.Dirs | Sort-Object Alloc -Descending); $n.Dirs.Clear(); foreach ($c in $sortedDirs) { [void]$n.Dirs.Add($c) }
    }
    [pscustomobject]@{ Root = $root; Top = @($top | Sort-Object Alloc -Descending | Select-Object -First 100); Exts = @($exts.Values | Sort-Object Alloc -Descending)
        Unreadable = [long]$unreadable; Links = [long]$links; Seconds = $watch.Elapsed.TotalSeconds; Cancelled = $false }
}

function Invoke-PxsaScan([string]$Target, [switch]$Quiet) {
    try { $full = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Target.Trim().Trim('"'))) } catch { Write-Host "  That isn't a folder path: $Target" -ForegroundColor Yellow; return $null }
    if (-not [IO.Directory]::Exists($full)) { Write-Host "  There's no folder at $full." -ForegroundColor Yellow; return $null }
    if (-not $Quiet) { Write-Host "  Scanning $full ..." -ForegroundColor DarkCyan }
    if ($script:PxsaFast) {
        $progress = New-Object PxdSa100.Progress
        $cts = New-Object Threading.CancellationTokenSource
        $job = [PxdSa100.Scanner]::Scan
        # Runs on a worker thread so this window can show progress meanwhile.
        $ps = [PowerShell]::Create()
        [void]$ps.AddScript({ param($p, $pr, $c) [PxdSa100.Scanner]::Scan($p, 0, $pr, $c) }).AddArgument($full).AddArgument($progress).AddArgument($cts.Token)
        $async = $ps.BeginInvoke()
        $shown = $false
        while (-not $async.AsyncWaitHandle.WaitOne(250)) {
            if (-not $Quiet) {
                try { [Console]::Write("`r  {0:N0} files, {1} ...      " -f $progress.Files, (Get-PxsaSize $progress.Bytes)); $shown = $true } catch { }
            }
        }
        $r = $ps.EndInvoke($async)
        $ps.Dispose()
        if ($shown) { try { [Console]::Write("`r" + (' ' * 60) + "`r") } catch { } }
        if ($r -and $r.Count -gt 0) { return $r[0] }
        return [PxdSa100.Scanner]::Scan($full, 0, $progress, [Threading.CancellationToken]::None)
    }
    Invoke-PxsaSlowScan $full
}

function Write-PxsaScanSummary($Scan) {
    $r = $Scan.Root
    $rate = if ($Scan.Seconds -gt 0) { $r.FileCount / $Scan.Seconds } else { 0 }
    Write-Host ''
    Write-Host "  $($r.FullPath)" -ForegroundColor White
    Write-Host ('  {0} on disk ({1} of data) in {2:N0} files and {3:N0} folders, read in {4:0.00} s ({5:N0} files/s{6})' -f (Get-PxsaSize $r.Alloc), (Get-PxsaSize $r.Size), $r.FileCount, $r.DirCount, $Scan.Seconds, $rate, $(if ($script:PxsaFast) { '' } else { ', plain PowerShell' })) -ForegroundColor Gray
    if ($Scan.Unreadable -gt 0) {
        $tail = if (Test-PxsaAdmin) { '.' } else { ' (run as administrator: A).' }
        Write-Host ('  {0:N0} folder(s) couldn''t be read{1}' -f $Scan.Unreadable, $tail) -ForegroundColor Yellow
    }
    if ($Scan.Links -gt 0) { Write-Host ('  {0:N0} junction(s) or folder link(s) not followed.' -f $Scan.Links) -ForegroundColor DarkGray }
}

# What's in a folder, biggest first: its folders, its files kept by name, and its smaller files as one line.
function Get-PxsaChildren($Node) {
    $rows = @()
    foreach ($d in $Node.Dirs) { $rows += [pscustomobject]@{ Kind = 'dir'; Name = $d.Name; Alloc = [long]$d.Alloc; Files = [long]$d.FileCount; Node = $d; Link = [bool]$d.Link; Unreadable = [bool]$d.Unreadable; Path = $d.FullPath } }
    foreach ($f in $Node.Files) { $rows += [pscustomobject]@{ Kind = 'file'; Name = $f.Name; Alloc = [long]$f.Alloc; Files = [long]1; Node = $null; Link = $false; Unreadable = $false; Path = $f.Path } }
    $rows = @($rows | Sort-Object Alloc -Descending)
    if ($Node.SmallCount -gt 0) { $rows += [pscustomobject]@{ Kind = 'small'; Name = ('{0:N0} smaller files' -f $Node.SmallCount); Alloc = [long]$Node.SmallAlloc; Files = [long]$Node.SmallCount; Node = $null; Link = $false; Unreadable = $false; Path = '' } }
    $rows
}

function Show-PxsaFolder($Node, [int]$Count) {
    Write-PxsaHead ("{0}   {1} on disk, {2:N0} files" -f $Node.FullPath, (Get-PxsaSize $Node.Alloc), $Node.FileCount)
    $rows = @(Get-PxsaChildren $Node)
    $w = Get-PxsaWidth
    $i = 0
    foreach ($r in ($rows | Select-Object -First $Count)) {
        $i++
        $f = if ($Node.Alloc -gt 0) { $r.Alloc / $Node.Alloc } else { 0 }
        $num = if ($r.Kind -eq 'dir' -and -not $r.Link) { '{0,3}' -f $i } else { '   ' }
        Write-Host "  $num " -NoNewline -ForegroundColor Cyan
        Write-PxsaBar $f 16 $(if ($f -gt 0.5) { 'Green' } else { 'DarkGreen' })
        Write-Host (' {0,6} {1,10}  ' -f (Get-PxsaPct $f), (Get-PxsaSize $r.Alloc)) -NoNewline -ForegroundColor Gray
        $label = switch ($r.Kind) { 'dir' { $r.Name + '\' } default { $r.Name } }
        $note = if ($r.Link) { '  (link, not followed)' } elseif ($r.Unreadable) { "  (couldn't be read)" } else { '' }
        $color = switch ($r.Kind) { 'dir' { 'White' } 'file' { 'Gray' } default { 'DarkGray' } }
        Write-Host (Get-PxsaCut $label ($w - 46 - $note.Length)) -NoNewline -ForegroundColor $color
        Write-Host $note -ForegroundColor $(if ($r.Unreadable) { 'Yellow' } else { 'DarkGray' })
    }
    if ($rows.Count -gt $Count) { Write-Host ('       ...and {0:N0} more' -f ($rows.Count - $Count)) -ForegroundColor DarkGray }
    if (-not $rows.Count) { Write-Host '  Empty.' -ForegroundColor DarkGray }
    @($rows | Select-Object -First $Count)
}

function Show-PxsaTopFiles($Scan, [int]$Count) {
    Write-PxsaHead "The $Count biggest files"
    $w = Get-PxsaWidth
    $i = 0
    foreach ($f in ($Scan.Top | Select-Object -First $Count)) {
        $i++
        Write-Host ('  {0,3}  {1,10}  ' -f $i, (Get-PxsaSize $f.Alloc)) -NoNewline -ForegroundColor Gray
        Write-Host (Get-PxsaCut $f.Path ($w - 21)) -ForegroundColor White
    }
    if (-not $Scan.Top -or -not @($Scan.Top).Count) { Write-Host '  No files.' -ForegroundColor DarkGray }
}

function Show-PxsaTypes($Scan, [int]$Count) {
    Write-PxsaHead 'Space by file type'
    $total = [double]$Scan.Root.Alloc; if ($total -le 0) { $total = 1 }
    foreach ($e in ($Scan.Exts | Select-Object -First $Count)) {
        $name = if ($e.Ext) { $e.Ext } else { '(none)' }
        Write-Host ('  {0,-12} ' -f (Get-PxsaCut $name 12)) -NoNewline -ForegroundColor White
        Write-PxsaBar ($e.Alloc / $total) 20 'Cyan'
        Write-Host (' {0,6} {1,10}  {2:N0} file{3}' -f (Get-PxsaPct ($e.Alloc / $total)), (Get-PxsaSize $e.Alloc), $e.Count, $(if ($e.Count -eq 1) { '' } else { 's' })) -ForegroundColor Gray
    }
}

# ======================================================================================
#  The usual waste
# ======================================================================================

function Get-PxsaPlaces {
    if ($script:PxsaTestRoot) {
        $root = [IO.Path]::GetFullPath($script:PxsaTestRoot).TrimEnd('\')
        $profiles = @()
        $users = Join-Path $root 'Users'
        if ([IO.Directory]::Exists($users)) {
            foreach ($d in ([IO.Directory]::GetDirectories($users) | Sort-Object)) {
                $n = [IO.Path]::GetFileName($d)
                if ($n -ne 'Public' -and $n -ne 'Default') { $profiles += [pscustomobject]@{ Name = $n; Path = $d } }
            }
        }
        return [pscustomobject]@{ SystemRoot = $root + '\'; Windows = (Join-Path $root 'Windows'); ProgramData = (Join-Path $root 'ProgramData'); Profiles = $profiles
            Drives = @($root + '\'); Test = $true; Admin = $true }
    }
    $win = [Environment]::GetFolderPath('Windows')
    $admin = Test-PxsaAdmin
    $system = Test-PxsaSystem
    $profiles = @()
    $list = @()
    try {
        foreach ($k in (Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' -ErrorAction Stop)) {
            $sid = $k.PSChildName
            if ($sid -notlike 'S-1-5-21-*' -or $sid -like '*.bak') { continue }
            $p = [Environment]::ExpandEnvironmentVariables([string]$k.GetValue('ProfileImagePath'))
            if ($p -and [IO.Directory]::Exists($p)) { $list += [pscustomobject]@{ Sid = $sid; Path = $p } }
        }
    } catch { }
    if ($system) {
        # Run by an RMM tool as SYSTEM: the signed-in user first (the console user, else whoever owns explorer.exe).
        $userName = ''
        try { $userName = [string](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName } catch { }
        if (-not $userName) {
            try {
                $ex = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction Stop | Select-Object -First 1
                if ($ex) { $o = Invoke-CimMethod -InputObject $ex -MethodName GetOwner -ErrorAction Stop; if ($o.User) { $userName = "$($o.Domain)\$($o.User)" } }
            } catch { }
        }
        if ($userName) {
            try {
                $sid = (New-Object Security.Principal.NTAccount($userName)).Translate([Security.Principal.SecurityIdentifier]).Value
                $hit = $list | Where-Object { $_.Sid -eq $sid } | Select-Object -First 1
                if ($hit) { $profiles += [pscustomobject]@{ Name = [IO.Path]::GetFileName($hit.Path); Path = $hit.Path } }
            } catch { }
        }
    } else {
        $me = [Environment]::GetFolderPath('UserProfile')
        $profiles += [pscustomobject]@{ Name = [IO.Path]::GetFileName($me); Path = $me }
    }
    if ($admin -or $system) {
        foreach ($p in $list) { if (-not ($profiles | Where-Object { $_.Path -eq $p.Path })) { $profiles += [pscustomobject]@{ Name = [IO.Path]::GetFileName($p.Path); Path = $p.Path } } }
    }
    [pscustomobject]@{ SystemRoot = [IO.Path]::GetPathRoot($win); Windows = $win; ProgramData = [Environment]::GetFolderPath('CommonApplicationData'); Profiles = $profiles
        Drives = @((Get-PxsaDrives) | ForEach-Object { $_.Root }); Test = $false; Admin = $admin }
}

function New-PxsaTarget([string]$Path, [string]$Kind = 'Contents', [string]$Pattern = '') { [pscustomobject]@{ Path = $Path; Kind = $Kind; Pattern = $Pattern } }

function New-PxsaItem([string]$Id, [string]$Name, [string]$Note, $Targets, [string]$How = 'None', [string]$User = '', [bool]$Admin = $false, [int]$Days = 0, [string]$Process = '', [string]$App = '') {
    [pscustomobject]@{ Id = $Id; Name = $Name; User = $User; Note = $Note; Targets = @($Targets); How = $How; NeedsAdmin = $Admin; OlderThanDays = $Days; Process = $Process; ProcessName = $App
        Size = [long]0; Files = [long]0; Unreadable = $false; Exists = $false; Blocked = '' }
}

function Get-PxsaBrowserCaches([string]$UserData) {
    $t = @()
    if (-not [IO.Directory]::Exists($UserData)) { return $t }
    try {
        foreach ($p in [IO.Directory]::GetDirectories($UserData)) {
            $n = [IO.Path]::GetFileName($p)
            if ($n -ne 'Default' -and $n -notlike 'Profile *' -and $n -ne 'Guest Profile') { continue }
            foreach ($c in 'Cache', 'Code Cache', 'GPUCache') { $x = Join-Path $p $c; if ([IO.Directory]::Exists($x)) { $t += New-PxsaTarget $x } }
        }
    } catch { }
    $t
}

# The same list (same ids, same notes) as the app's WasteFinder.
function Get-PxsaWasteItems($P) {
    $items = @()
    $many = @($P.Profiles).Count -gt 1
    foreach ($u in $P.Profiles) {
        $who = if ($many) { $u.Name } else { '' }
        $local = Join-Path $u.Path 'AppData\Local'
        $items += New-PxsaItem 'usertemp' 'Your temp files' 'Left behind by installers and apps. Files older than a day are safe to delete; newer ones may still be in use, so they stay.' (New-PxsaTarget (Join-Path $local 'Temp')) 'Permanent' $who $false 1
        $items += New-PxsaItem 'appdumps' 'App crash dumps' 'Snapshots of apps that crashed. Safe to remove (they go to the Recycle Bin).' (New-PxsaTarget (Join-Path $local 'CrashDumps')) 'RecycleBin' $who
        $items += New-PxsaItem 'userwer' 'Error reports' "Your apps' problem reports, waiting or already sent. Safe to delete." @((New-PxsaTarget (Join-Path $local 'Microsoft\Windows\WER\ReportQueue')), (New-PxsaTarget (Join-Path $local 'Microsoft\Windows\WER\ReportArchive'))) 'Permanent' $who
        $items += New-PxsaItem 'edge' 'Microsoft Edge cache' 'Web pages, scripts and pictures Microsoft Edge keeps to load sites faster. Safe to delete while Microsoft Edge is closed; sign-ins, passwords and history stay.' (Get-PxsaBrowserCaches (Join-Path $local 'Microsoft\Edge\User Data')) 'Permanent' $who $false 0 'msedge' 'Microsoft Edge'
        $items += New-PxsaItem 'chrome' 'Google Chrome cache' 'Web pages, scripts and pictures Google Chrome keeps to load sites faster. Safe to delete while Google Chrome is closed; sign-ins, passwords and history stay.' (Get-PxsaBrowserCaches (Join-Path $local 'Google\Chrome\User Data')) 'Permanent' $who $false 0 'chrome' 'Google Chrome'
        $ff = @()
        $ffp = Join-Path $local 'Mozilla\Firefox\Profiles'
        if ([IO.Directory]::Exists($ffp)) { try { foreach ($d in [IO.Directory]::GetDirectories($ffp)) { $c = Join-Path $d 'cache2'; if ([IO.Directory]::Exists($c)) { $ff += New-PxsaTarget $c } } } catch { } }
        $items += New-PxsaItem 'firefox' 'Firefox cache' 'Web pages and pictures Firefox keeps to load sites faster. Safe to delete while Firefox is closed; sign-ins and history stay.' $ff 'Permanent' $who $false 0 'firefox' 'Firefox'
        $items += New-PxsaItem 'thumbs' 'Thumbnail cache' 'Picture previews Explorer keeps. Safe to delete; Explorer makes them again (files it has open are skipped).' (New-PxsaTarget (Join-Path $local 'Microsoft\Windows\Explorer') 'Pattern' 'thumbcache_*.db') 'Permanent' $who
        $items += New-PxsaItem 'downloads' 'Downloads folder' 'Your own downloads: old installers and zips pile up here. Look through it yourself; nothing is deleted automatically.' (New-PxsaTarget (Join-Path $u.Path 'Downloads')) 'None' $who
    }
    $w = $P.Windows
    $items += New-PxsaItem 'wintemp' 'Windows temp files' 'Temp files from Windows, services and installers. Files older than a day are safe to delete.' (New-PxsaTarget (Join-Path $w 'Temp')) 'Permanent' '' $true 1
    $items += New-PxsaItem 'wu' 'Windows Update downloads' 'Update files already installed (or downloaded again if needed). Safe to delete; files in use by an update running now are skipped. Nothing is stopped.' (New-PxsaTarget (Join-Path $w 'SoftwareDistribution\Download')) 'Permanent' '' $true
    $items += New-PxsaItem 'do' 'Delivery Optimization cache' 'Copies of updates Windows shares with other PCs. Safe to clear; Windows downloads again what it needs.' @((New-PxsaTarget (Join-Path $w 'ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache')), (New-PxsaTarget (Join-Path $w 'SoftwareDistribution\DeliveryOptimization'))) 'DeliveryOptimization' '' $true
    $items += New-PxsaItem 'winold' 'Windows.old (previous Windows)' 'Your previous Windows, kept for going back after an upgrade (about 10 days). Remove it in Settings > System > Storage > Temporary files > Previous Windows installation(s).' (New-PxsaTarget (Join-Path $P.SystemRoot 'Windows.old'))
    foreach ($d in $P.Drives) {
        $items += New-PxsaItem 'recycle' "Recycle Bin ($($d.TrimEnd('\')))" 'Files you deleted. Emptying it removes them for good.' (New-PxsaTarget $d) 'EmptyRecycleBin'
    }
    $items += New-PxsaItem 'hiber' 'Hibernation file (hiberfil.sys)' 'Holds memory while the PC hibernates; also used by Fast startup. If you never hibernate, "powercfg -h off" in an administrator prompt removes it (Fast startup goes too). Not done here.' (New-PxsaTarget (Join-Path $P.SystemRoot 'hiberfil.sys') 'File')
    $items += New-PxsaItem 'pagefile' 'Page file (pagefile.sys, swapfile.sys)' 'Virtual memory, managed by Windows. Leave it; change its size only in System > Advanced system settings > Performance.' @((New-PxsaTarget (Join-Path $P.SystemRoot 'pagefile.sys') 'File'), (New-PxsaTarget (Join-Path $P.SystemRoot 'swapfile.sys') 'File'))
    $items += New-PxsaItem 'dumps' 'Crash dumps (Windows)' 'Memory snapshots from blue screens and driver hangs. Only useful for finding the cause; safe to remove (they go to the Recycle Bin).' @((New-PxsaTarget (Join-Path $w 'MEMORY.DMP') 'File'), (New-PxsaTarget (Join-Path $w 'Minidump')), (New-PxsaTarget (Join-Path $w 'LiveKernelReports'))) 'RecycleBin' '' $true
    $wer = Join-Path $P.ProgramData 'Microsoft\Windows\WER'
    $items += New-PxsaItem 'wer' 'Error reports (Windows)' 'Problem reports waiting to be sent to Microsoft, or already sent. Safe to delete.' @((New-PxsaTarget (Join-Path $wer 'ReportQueue')), (New-PxsaTarget (Join-Path $wer 'ReportArchive')), (New-PxsaTarget (Join-Path $wer 'Temp'))) 'Permanent' '' $true
    $items += New-PxsaItem 'patchcache' 'Installer patch cache ($PatchCache$)' 'Backup copies of files patched by Windows Installer. Never delete anything in C:\Windows\Installer by hand: programs need it to update and uninstall. Reported only.' (New-PxsaTarget (Join-Path $w 'Installer\$PatchCache$'))
    $items += New-PxsaItem 'winsxs' 'Component store (WinSxS)' 'Windows'' own components. Most of it is shared with Windows itself (hard links), so its size looks bigger than it is. Never delete it; as administrator, "DISM /Online /Cleanup-Image /AnalyzeComponentStore" says how much could be cleaned.' (New-PxsaTarget (Join-Path $w 'WinSxS'))
    $items
}

# Sum of the files under a folder (not into junctions); -Before counts only files last changed before it.
function Measure-PxsaFolder([string]$Dir, $Before = $null) {
    $res = [pscustomobject]@{ Size = [long]0; Alloc = [long]0; Files = [long]0; Unreadable = $false; Exists = $false }
    if (-not [IO.Directory]::Exists($Dir)) { return $res }
    $res.Exists = $true
    if ($null -eq $Before -and $script:PxsaFast) {
        $r = [PxdSa100.Scanner]::Scan($Dir, 4, (New-Object PxdSa100.Progress), [Threading.CancellationToken]::None)
        $res.Size = $r.Root.Size; $res.Alloc = $r.Root.Alloc; $res.Files = $r.Root.FileCount; $res.Unreadable = $r.Unreadable -gt 0
        return $res
    }
    $stack = New-Object Collections.Stack
    $stack.Push([IO.DirectoryInfo]$Dir)
    while ($stack.Count -gt 0) {
        $d = $stack.Pop()
        try {
            foreach ($e in $d.EnumerateFileSystemInfos()) {
                if ($e.Attributes -band [IO.FileAttributes]::Directory) {
                    if (-not ($e.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $stack.Push($e) }
                    continue
                }
                if ($null -ne $Before -and $e.LastWriteTime -ge $Before) { continue }
                $res.Size += $e.Length; $res.Alloc += (Get-PxsaRound $e.Length); $res.Files++
            }
        } catch { $res.Unreadable = $true }
    }
    $res
}

function Test-PxsaRunning([string]$Name) {
    if ($script:PxsaTestRoot) { return $script:PxsaTestRunning -contains $Name }
    [bool](Get-Process -Name $Name -ErrorAction SilentlyContinue)
}

function Get-PxsaRecycleBinFolder([string]$Drive) {
    if ($script:PxsaTestRoot) { return (Join-Path $Drive '$Recycle.Bin') }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Join-Path $Drive ('$Recycle.Bin\' + $sid)
}

function Measure-PxsaItem($Item, $P) {
    $size = [long]0; $files = [long]0; $bad = $false; $exists = $false
    foreach ($t in $Item.Targets) {
        switch ($t.Kind) {
            'File' {
                # Read from the folder's listing: hiberfil.sys and pagefile.sys can't be opened.
                try {
                    $parent = [IO.Path]::GetDirectoryName($t.Path); $leaf = [IO.Path]::GetFileName($t.Path)
                    $f = ([IO.DirectoryInfo]$parent).GetFiles($leaf) | Select-Object -First 1
                    if ($f) { $size += Get-PxsaRound $f.Length; $files++; $exists = $true }
                } catch { }
            }
            'Pattern' {
                if ([IO.Directory]::Exists($t.Path)) {
                    try { foreach ($f in ([IO.DirectoryInfo]$t.Path).GetFiles($t.Pattern)) { $size += Get-PxsaRound $f.Length; $files++; $exists = $true } } catch { $bad = $true }
                }
            }
            default {
                $dir = if ($Item.How -eq 'EmptyRecycleBin') { Get-PxsaRecycleBinFolder $t.Path } else { $t.Path }
                if ($Item.How -eq 'EmptyRecycleBin') { $exists = $true }
                $before = if ($Item.OlderThanDays -gt 0) { (Get-Date).AddDays(-$Item.OlderThanDays) } else { $null }
                $m = Measure-PxsaFolder $dir $before
                if ($Item.How -eq 'EmptyRecycleBin' -and $m.Exists) {
                    # The Recycle Bin's own desktop.ini files don't count.
                    try { foreach ($ini in ([IO.DirectoryInfo]$dir).GetFiles('desktop.ini', [IO.SearchOption]::AllDirectories)) { $m.Alloc -= Get-PxsaRound $ini.Length; $m.Files-- } } catch { }
                }
                $size += $m.Alloc; $files += $m.Files; $bad = $bad -or $m.Unreadable; $exists = $exists -or $m.Exists
            }
        }
    }
    $Item.Size = [Math]::Max([long]0, $size); $Item.Files = [Math]::Max([long]0, $files); $Item.Unreadable = $bad; $Item.Exists = $exists; $Item.Blocked = ''
    if ($Item.How -ne 'None') {
        if ($Item.NeedsAdmin -and -not $P.Admin) { $Item.Blocked = 'needs administrator rights' }
        elseif ($Item.Process -and (Test-PxsaRunning $Item.Process)) { $Item.Blocked = "close $($Item.ProcessName) first" }
    }
}

function Test-PxsaCanClean($Item) { $Item.How -ne 'None' -and -not $Item.Blocked -and $Item.Size -gt 0 }

function Get-PxsaHowText($Item) {
    switch ($Item.How) {
        'Permanent' { 'deleted permanently (not to the Recycle Bin)' }
        'RecycleBin' { if ($script:PxsaTestRoot -or -not $script:PxsaFast) { 'deleted permanently (not to the Recycle Bin)' } else { 'moved to the Recycle Bin' } }
        'EmptyRecycleBin' { 'removed from the Recycle Bin for good' }
        'DeliveryOptimization' { 'cleared by Windows (Delivery Optimization), for good' }
        default { 'only reported' }
    }
}

function Find-PxsaWaste($P) {
    $all = @(Get-PxsaWasteItems $P)
    foreach ($i in $all) { Measure-PxsaItem $i $P }
    @($all | Where-Object { $_.Exists -or $_.Unreadable -or $_.Id -eq 'recycle' })
}

function Get-PxsaItemName($Item) { if ($Item.User) { "$($Item.Name) ($($Item.User))" } else { $Item.Name } }

function Show-PxsaWaste($Items) {
    Write-PxsaHead 'The usual waste'
    $can = [long]0; $more = [long]0
    foreach ($i in $Items) { if (Test-PxsaCanClean $i) { $can += $i.Size } else { $more += $i.Size } }
    Write-Host ('  {0} can be cleaned up safely, {1} more to know about.' -f (Get-PxsaSize $can), (Get-PxsaSize $more)) -ForegroundColor White
    Write-Host ''
    $w = Get-PxsaWidth
    $n = 0
    foreach ($i in ($Items | Sort-Object @{ Expression = { Test-PxsaCanClean $_ }; Descending = $true }, @{ Expression = { $_.Size }; Descending = $true })) {
        $n++
        $sz = if ($i.Size -eq 0 -and $i.Unreadable) { '?' } else { Get-PxsaSize $i.Size }
        $tag = if ($i.Blocked) { "[$($i.Blocked)]" } elseif ($i.How -eq 'None') { '[report only]' } elseif ($i.Size -gt 0) { '[safe to clean]' } else { '' }
        $tagColor = if ($i.Blocked) { 'Yellow' } elseif ($i.How -eq 'None') { 'DarkGray' } else { 'Green' }
        Write-Host ('  {0,10}  ' -f $sz) -NoNewline -ForegroundColor $(if (Test-PxsaCanClean $i) { 'Green' } else { 'Gray' })
        Write-Host (Get-PxsaItemName $i) -NoNewline -ForegroundColor White
        Write-Host "  $tag" -ForegroundColor $tagColor
        if ($i.Size -eq 0 -and $i.Unreadable) { Write-Host '              (needs administrator to measure)' -ForegroundColor DarkGray }
        foreach ($line in (Split-PxsaText $i.Note ($w - 16))) { Write-Host "              $line" -ForegroundColor DarkGray }
    }
}

function Split-PxsaText([string]$Text, [int]$Width) {
    if ($Width -lt 30) { $Width = 30 }
    $lines = @(); $cur = ''
    foreach ($word in ($Text -split ' ')) {
        if ($cur.Length + $word.Length + 1 -gt $Width -and $cur) { $lines += $cur; $cur = $word } else { $cur = if ($cur) { "$cur $word" } else { $word } }
    }
    if ($cur) { $lines += $cur }
    $lines
}

# ======================================================================================
#  Clean-up (only inside the item's own folders; junctions are never followed; files in use are skipped)
# ======================================================================================

function Test-PxsaInside([string]$Path, [string]$Root) {
    $p = [IO.Path]::GetFullPath($Path).TrimEnd('\'); $r = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $p -eq $r -or $p.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Remove-PxsaFile([string]$File, $Result) {
    try {
        $fi = New-Object IO.FileInfo($File)
        $len = $fi.Length
        if ($fi.Attributes -band [IO.FileAttributes]::ReadOnly) { $fi.Attributes = [IO.FileAttributes]::Normal }
        [IO.File]::Delete($File)
        if (-not [IO.File]::Exists($File)) { $Result.Freed += (Get-PxsaRound $len); $Result.Deleted++ } else { $Result.Skipped++ }
    } catch { $Result.Skipped++ }
}

# Deletes what's inside $Dir (never $Dir itself): files changed before $Before when given, then folders left empty
# that were made before it. Junctions and links are skipped, and what they point to is never touched.
function Remove-PxsaContents([string]$Dir, $Before, $Result) {
    if (-not [IO.Directory]::Exists($Dir)) { return }
    $folders = New-Object Collections.ArrayList
    $stack = New-Object Collections.Stack
    $stack.Push([IO.DirectoryInfo]$Dir)
    while ($stack.Count -gt 0) {
        $d = $stack.Pop()
        $entries = @()
        try { $entries = @($d.GetFileSystemInfos()) } catch { $Result.Skipped++; continue }
        foreach ($e in $entries) {
            if ($e.Attributes -band [IO.FileAttributes]::Directory) {
                if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
                $stack.Push($e)
                if ($null -eq $Before -or $e.CreationTime -lt $Before) { [void]$folders.Add($e.FullName) }
                continue
            }
            if ($null -ne $Before -and $e.LastWriteTime -ge $Before) { continue }
            if (-not (Test-PxsaInside $e.FullName $Dir)) { continue }
            Remove-PxsaFile $e.FullName $Result
        }
    }
    foreach ($f in ($folders | Sort-Object Length -Descending)) {
        try { if (-not [IO.Directory]::EnumerateFileSystemEntries($f).GetEnumerator().MoveNext()) { [IO.Directory]::Delete($f, $false) } } catch { }
    }
}

function Invoke-PxsaClean($Item, $P) {
    $r = [pscustomobject]@{ Freed = [long]0; Deleted = [long]0; Skipped = [long]0; Message = '' }
    if (-not (Test-PxsaCanClean $Item)) { $r.Message = 'Only reported; nothing was deleted.'; return $r }
    if ($Item.NeedsAdmin -and -not $P.Admin) { $r.Message = 'Needs administrator rights.'; return $r }
    if ($Item.Process -and (Test-PxsaRunning $Item.Process)) { $r.Message = "$($Item.ProcessName) is open. Close it and try again."; return $r }
    switch ($Item.How) {
        'EmptyRecycleBin' {
            foreach ($t in $Item.Targets) {
                if ($P.Test) {
                    Remove-PxsaContents (Join-Path $t.Path '$Recycle.Bin') $null $r
                } else {
                    $before = $Item.Size
                    try { Clear-RecycleBin -DriveLetter ($t.Path.Substring(0, 1)) -Force -ErrorAction Stop; $r.Freed += $before; $r.Deleted++ } catch { $r.Skipped++ }
                }
            }
        }
        'DeliveryOptimization' {
            $done = $false
            if (-not $P.Test -and (Get-Command Delete-DeliveryOptimizationCache -ErrorAction SilentlyContinue)) {
                try { Delete-DeliveryOptimizationCache -Force -ErrorAction Stop; $done = $true; $r.Freed = $Item.Size; $r.Deleted = $Item.Files } catch { }
            }
            if (-not $done) { foreach ($t in $Item.Targets) { Remove-PxsaContents $t.Path $null $r } }
        }
        'RecycleBin' {
            foreach ($t in $Item.Targets) {
                $files = @()
                if ($t.Kind -eq 'File') { if ([IO.File]::Exists($t.Path)) { $files += $t.Path } }
                elseif ([IO.Directory]::Exists($t.Path)) {
                    # One file at a time, never into a link.
                    $stack = New-Object Collections.Stack; $stack.Push([IO.DirectoryInfo]$t.Path)
                    while ($stack.Count -gt 0) {
                        $d = $stack.Pop()
                        try { foreach ($e in $d.GetFileSystemInfos()) { if ($e.Attributes -band [IO.FileAttributes]::Directory) { if (-not ($e.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $stack.Push($e) } } else { $files += $e.FullName } } } catch { $r.Skipped++ }
                    }
                }
                foreach ($f in $files) {
                    if ($script:PxsaTestRoot -or -not $script:PxsaFast) { Remove-PxsaFile $f $r; continue }
                    $len = 0; try { $len = (New-Object IO.FileInfo($f)).Length } catch { }
                    if ([PxdSa100.Shell]::Recycle($f)) { $r.Freed += (Get-PxsaRound $len); $r.Deleted++ } else { $r.Skipped++ }
                }
            }
        }
        'Permanent' {
            $before = if ($Item.OlderThanDays -gt 0) { (Get-Date).AddDays(-$Item.OlderThanDays) } else { $null }
            foreach ($t in $Item.Targets) {
                if ($t.Kind -eq 'Pattern') {
                    if ([IO.Directory]::Exists($t.Path)) { try { foreach ($f in ([IO.DirectoryInfo]$t.Path).GetFiles($t.Pattern)) { Remove-PxsaFile $f.FullName $r } } catch { $r.Skipped++ } }
                } elseif ($t.Kind -eq 'File') {
                    if ([IO.File]::Exists($t.Path)) { Remove-PxsaFile $t.Path $r }
                } else { Remove-PxsaContents $t.Path $before $r }
            }
        }
    }
    $r.Message = "Freed $(Get-PxsaSize $r.Freed)." + $(if ($r.Skipped -gt 0) { " $($r.Skipped) in use or not allowed, skipped." } else { '' })
    $r
}

function Invoke-PxsaCleanMenu($Items, $P) {
    $can = @($Items | Where-Object { Test-PxsaCanClean $_ } | Sort-Object Size -Descending)
    Write-PxsaHead 'Clean up'
    if (-not $can.Count) { Write-Host '  Nothing to clean up right now.' -ForegroundColor DarkGray; return }
    $i = 0
    foreach ($c in $can) {
        $i++
        Write-Host ('  {0,2}  {1,10}  ' -f $i, (Get-PxsaSize $c.Size)) -NoNewline -ForegroundColor Cyan
        Write-Host (Get-PxsaItemName $c) -NoNewline -ForegroundColor White
        Write-Host "  ($(Get-PxsaHowText $c))" -ForegroundColor DarkGray
    }
    Write-Host ''
    Write-Host '  Numbers to clean (like 1 3 4), * for all of them, Enter to go back. Each one is asked again.' -ForegroundColor DarkGray
    $k = Read-PxsaKey '  Clean'
    if (-not $k -or $k -match '^(?i)[bq]$') { return }
    $pick = @()
    if ($k -eq '*') { $pick = $can } else { foreach ($t in ($k -split '[ ,;]+')) { $n = 0; if ([int]::TryParse($t, [ref]$n) -and $n -ge 1 -and $n -le $can.Count) { $pick += $can[$n - 1] } } }
    $freed = [long]0
    foreach ($c in $pick) {
        if ($script:PxsaEof) { break }
        if (-not (Test-PxsaYes ("  {0}: {1}, {2}. Clean it? (y/N)" -f (Get-PxsaItemName $c), (Get-PxsaSize $c.Size), (Get-PxsaHowText $c)))) { Write-Host '     Skipped.' -ForegroundColor DarkGray; continue }
        $r = Invoke-PxsaClean $c $P
        $freed += $r.Freed
        Write-Host "     $($r.Message)" -ForegroundColor $(if ($r.Skipped -gt 0) { 'Yellow' } else { 'Green' })
        Measure-PxsaItem $c $P
    }
    if ($pick.Count) { Write-Host ("  Freed {0} in all." -f (Get-PxsaSize $freed)) -ForegroundColor Green }
}

function Invoke-PxsaWasteMenu {
    $P = Get-PxsaPlaces
    Write-Host '  Looking for the usual waste...' -ForegroundColor DarkCyan
    $items = @(Find-PxsaWaste $P)
    while (-not $script:PxsaEof) {
        Show-PxsaWaste $items
        Write-Host ''
        Write-Host "  C clean up   R look again   $(if (-not $P.Admin) { 'A restart as administrator   ' })B back   Q quit" -ForegroundColor DarkGray
        $k = Read-PxsaKey '  Choose'
        switch -Regex ($k) {
            '^(?i)c$' { Invoke-PxsaCleanMenu $items $P; break }
            '^(?i)r$' { $items = @(Find-PxsaWaste $P); break }
            '^(?i)a$' { if (-not $P.Admin -and (Restart-PxsaElevated)) { Write-Host '  Opened an elevated window; this one can be closed.' -ForegroundColor Green }; break }
            '^(?i)q$' { $script:PxsaQuit = $true; return }
            default { return }
        }
    }
}

# ======================================================================================
#  Console
# ======================================================================================

function Invoke-PxsaBrowse($Scan) {
    $node = $Scan.Root
    $count = $Top
    while (-not $script:PxsaEof) {
        $rows = @(Show-PxsaFolder $node $count)
        Write-Host ''
        Write-Host '  Number to open a folder   B back   F biggest files   T file types   W the usual waste' -ForegroundColor DarkGray
        Write-Host "  M show more   O open in Explorer   R scan again   D drives   $(if (-not (Test-PxsaAdmin)) { 'A restart as administrator   ' })Q quit" -ForegroundColor DarkGray
        $k = Read-PxsaKey '  Choose'
        $n = 0
        if ([int]::TryParse($k, [ref]$n)) {
            if ($n -ge 1 -and $n -le $rows.Count -and $rows[$n - 1].Kind -eq 'dir' -and -not $rows[$n - 1].Link) { $node = $rows[$n - 1].Node; $count = $Top }
            else { Write-Host "  $n isn't a folder you can open." -ForegroundColor Yellow }
            continue
        }
        switch -Regex ($k) {
            '^(?i)b$' { if ($node.Parent) { $node = $node.Parent; $count = $Top } else { return }; break }
            '^(?i)f$' { Show-PxsaTopFiles $Scan ([Math]::Max($Top, 20)); break }
            '^(?i)t$' { Show-PxsaTypes $Scan $Top; break }
            '^(?i)w$' { Invoke-PxsaWasteMenu; if ($script:PxsaQuit) { return }; break }
            '^(?i)m$' { $count += $Top; break }
            '^(?i)o$' { try { Start-Process explorer.exe -ArgumentList ('"' + $node.FullPath + '"') } catch { }; break }
            '^(?i)r$' { $s = Invoke-PxsaScan $Scan.Root.FullPath; if ($s) { $Scan = $s; $script:PxsaScan = $s; $node = $s.Root; Write-PxsaScanSummary $s }; break }
            '^(?i)d$' { return }
            '^(?i)a$' { if (-not (Test-PxsaAdmin) -and (Restart-PxsaElevated)) { Write-Host '  Opened an elevated window; this one can be closed.' -ForegroundColor Green }; break }
            '^(?i)q$' { $script:PxsaQuit = $true; return }
            '^$' { break }
            default { Write-Host "  Didn't get that: $k" -ForegroundColor Yellow }
        }
    }
}

function Start-PxsaConsole {
    Show-PdHeader -Name 'Space Analyzer' -Version '1.0.0' -Command 'irm pxdtek.com/sa|iex'
    if ($script:PxsaInMemory) { Write-Host '  In-memory: nothing is saved on this PC.' -ForegroundColor DarkGray }
    if ($script:PxsaTestRoot) { Write-Host "  Test mode: the usual waste is looked for in $($script:PxsaTestRoot) only." -ForegroundColor Yellow }
    if (-not (Test-PxsaAdmin)) { Write-Host '  Not running as administrator: some folders can''t be read and Windows'' own caches can''t be cleaned (A restarts elevated).' -ForegroundColor DarkGray }
    $script:PxsaQuit = $false
    if ($Path) {
        $s = Invoke-PxsaScan $Path
        if ($s) { $script:PxsaScan = $s; Write-PxsaScanSummary $s; Invoke-PxsaBrowse $s }
    }
    while (-not $script:PxsaEof -and -not $script:PxsaQuit) {
        $drives = @(Get-PxsaDrives)
        Show-PxsaDrives $drives
        Write-Host ''
        Write-Host "  Number to scan a drive   P a folder   W the usual waste   $(if ($script:PxsaScan) { 'S back to the last scan   ' })$(if (-not (Test-PxsaAdmin)) { 'A restart as administrator   ' })Q quit" -ForegroundColor DarkGray
        $k = Read-PxsaKey '  Choose'
        $n = 0
        if ([int]::TryParse($k, [ref]$n)) {
            if ($n -ge 1 -and $n -le $drives.Count) {
                $s = Invoke-PxsaScan $drives[$n - 1].Root
                if ($s) { $script:PxsaScan = $s; Write-PxsaScanSummary $s; Invoke-PxsaBrowse $s }
            } else { Write-Host "  There's no drive $n." -ForegroundColor Yellow }
            continue
        }
        switch -Regex ($k) {
            '^(?i)p$' {
                $where = Read-PxsaKey '  Folder'
                if ($where -and -not $script:PxsaEof) { $s = Invoke-PxsaScan $where; if ($s) { $script:PxsaScan = $s; Write-PxsaScanSummary $s; Invoke-PxsaBrowse $s } }
                break
            }
            '^(?i)w$' { Invoke-PxsaWasteMenu; break }
            '^(?i)s$' { if ($script:PxsaScan) { Invoke-PxsaBrowse $script:PxsaScan }; break }
            '^(?i)a$' { if (-not (Test-PxsaAdmin) -and (Restart-PxsaElevated)) { Write-Host '  Opened an elevated window; this one can be closed.' -ForegroundColor Green }; break }
            '^(?i)q$' { return }
            '^$' { return }
            default { if ([IO.Directory]::Exists($k)) { $s = Invoke-PxsaScan $k; if ($s) { $script:PxsaScan = $s; Write-PxsaScanSummary $s; Invoke-PxsaBrowse $s } } else { Write-Host "  Didn't get that: $k" -ForegroundColor Yellow } }
        }
    }
}

# ---- PD header ---------------------------------------------------------------------------------------------------
# The pxd logo beside the tool's name, shown when a pxdtek CLI tool starts. The master copy is cli/PdHeader.ps1 in the
# pxdtek repo; each tool carries a copy (they run through irm | iex, so they can't load it). tools/sync-header.ps1
# copies it into them.
# Ten drawings of the logo, from tools/pdlogos.mjs: solid blocks, braille dots, shades and plain ASCII, tilted like the
# logo or upright, the pxd in white and the ring in cyan. They're stored as gzip + base64 so this stays ASCII and
# irm | iex never trips on encoding. Each time, one is picked at random from those that fit the window (braille only
# in Windows Terminal or VS Code, whose fonts have the dots); a window too narrow for any gets a one-line header.
# Nothing is kept between runs: under irm | iex the script scope is the caller's own session.

# A random logo no wider than $Room (@{ Style; Width; Text = lines; Color = lines }), or $null if none fits.
function Get-PdLogo([int]$Room = [int]::MaxValue) {
    $logos = @(
        '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'
    ) -join ''
    $all = @()
    try {
        $gz = New-Object IO.Compression.GZipStream((New-Object IO.MemoryStream(, [Convert]::FromBase64String($logos))), ([IO.Compression.CompressionMode]::Decompress))
        $body = (New-Object IO.StreamReader($gz, [Text.Encoding]::UTF8)).ReadToEnd()
        foreach ($chunk in ($body -split "`n~`n")) {
            $l = $chunk -split "`n"
            $head = $l[0] -split ' '
            $n = [int]$head[2]
            $all += @{ Style = $head[0]; Width = [int]$head[1]; Text = $l[1..$n]; Color = $l[($n + 1)..(2 * $n)] }
        }
    } catch { return $null }
    $dots = [bool]($env:WT_SESSION -or $env:TERM_PROGRAM -eq 'vscode')
    $fit = @($all | Where-Object { ($dots -or $_.Style -ne 'd') -and $_.Width -le $Room })
    if (-not $fit.Count) { return $null }
    $fit[(Get-Random -Maximum $fit.Count)]
}

# Line $Row of a logo in its colours (W the pxd, C the ring, D its faint edge), no newline; -Pad fills it to the
# logo's width. A row past the logo's end writes only the padding.
function Write-PdLogoLine($Logo, [int]$Row, [switch]$Pad) {
    $line = ''; $mask = ''
    if ($Row -ge 0 -and $Row -lt $Logo.Text.Count) { $line = [string]$Logo.Text[$Row]; $mask = [string]$Logo.Color[$Row] }
    $colors = @{ 'W' = 'White'; 'C' = 'Cyan'; 'D' = 'DarkCyan' }
    # one colour run at a time; spaces join whichever run they're in
    $run = ''; $col = 'Cyan'
    for ($k = 0; $k -lt $line.Length; $k++) {
        $c = $col
        if ($line[$k] -ne ' ' -and $k -lt $mask.Length -and $colors.ContainsKey([string]$mask[$k])) { $c = $colors[[string]$mask[$k]] }
        if ($c -ne $col -and $run) { Write-Host $run -NoNewline -ForegroundColor $col; $run = '' }
        $col = $c; $run += $line[$k]
    }
    if ($run) { Write-Host $run -NoNewline -ForegroundColor $col }
    if ($Pad -and $line.Length -lt $Logo.Width) { Write-Host (' ' * ($Logo.Width - $line.Length)) -NoNewline }
}

function Show-PdHeader {
    param([string]$Name, [string]$Version = '', [string[]]$About = @(), [string]$Command = '')
    $text = New-Object System.Collections.Generic.List[object]
    $text.Add(@($Name, 'White'))
    $text.Add(@(((@($Version, 'pxdtek.com') | Where-Object { $_ }) -join '  -  '), 'DarkGray'))
    if ($About) { $text.Add(@('', 'Gray')); foreach ($a in $About) { $text.Add(@($a, 'Gray')) } }
    if ($Command) { $text.Add(@('', 'Gray')); $text.Add(@($Command, 'DarkCyan')) }
    $textWidth = ($text | ForEach-Object { $_[0].Length } | Measure-Object -Maximum).Maximum

    $w = 0
    try { $w = $Host.UI.RawUI.WindowSize.Width } catch {}
    $logo = Get-PdLogo $(if ($w -gt 0) { $w - $textWidth - 7 } else { [int]::MaxValue })

    Write-Host ''
    if (-not $logo) {
        Write-Host '  (pd) ' -NoNewline -ForegroundColor Cyan
        Write-Host $Name -NoNewline -ForegroundColor White
        Write-Host $(if ($Version) { "  $Version" } else { '' }) -ForegroundColor DarkGray
        Write-Host ''
        return
    }
    $rows = $logo.Text.Count
    $top = [Math]::Max(0, [Math]::Floor(($rows - $text.Count) / 2))
    for ($i = 0; $i -lt [Math]::Max($rows, $top + $text.Count); $i++) {
        $j = $i - $top
        $say = $j -ge 0 -and $j -lt $text.Count -and $text[$j][0]
        Write-Host '  ' -NoNewline
        Write-PdLogoLine $logo $i -Pad:$say
        if ($say) { Write-Host ('    ' + $text[$j][0]) -NoNewline -ForegroundColor $text[$j][1] }
        Write-Host ''
    }
    Write-Host ''
}
# ---- end of PD header --------------------------------------------------------------------------------------------

# ======================================================================================
#  Entry
# ======================================================================================

if ($env:OS -ne 'Windows_NT') { Write-Host 'pxd Space Analyzer looks at Windows drives, so it runs on Windows only.' -ForegroundColor Yellow; return }
Initialize-PxsaFast

if ($Report) {
    Show-PdHeader -Name 'Space Analyzer' -Version '1.0.0' -Command 'irm pxdtek.com/sa|iex'
    Show-PxsaDrives @(Get-PxsaDrives)
    if ($Path) {
        $s = Invoke-PxsaScan $Path -Quiet
        if ($s) {
            Write-PxsaScanSummary $s
            [void](Show-PxsaFolder $s.Root $Top)
            Show-PxsaTopFiles $s ([Math]::Min($Top, 20))
            Show-PxsaTypes $s ([Math]::Min($Top, 15))
        }
    }
    if ($Waste) { Show-PxsaWaste @(Find-PxsaWaste (Get-PxsaPlaces)) }
    Write-Host ''
    return
}

Start-PxsaConsole
