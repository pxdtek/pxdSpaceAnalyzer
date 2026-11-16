using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using pxdSpaceAnalyzer.Core;
using pxdSpaceAnalyzer.Views;

namespace pxdSpaceAnalyzer;

/// <summary>
/// --self-test DIR [--sample]: builds a made-up folder tree in DIR and checks the scan totals (sizes, sizes on disk,
/// file counts, long paths, a junction that must not be followed, a folder that can't be read), the treemap layout,
/// the usual-waste finder and the clean-up against a made-up PC (its Windows, ProgramData, Users and Recycle Bin all
/// inside DIR), and deleting from the tree. Then a real read-only scan of a small Windows folder for speed. Renders
/// every page and the confirm dialog to PNGs on the headless platform (nothing on screen). --sample = README
/// screenshots from made-up data. Nothing outside DIR is changed. Exit code 0 = all passed.
/// </summary>
internal static class SelfTest
{
    public static string? Dir { get; set; }
    public static bool Sample { get; set; }
    public static bool Active { get; private set; }
    public static int ExitCode { get; private set; } = 1;

    private static readonly StringBuilder R = new();
    private static int _failed;

    private static void Check(string name, bool ok, string? detail = null)
    {
        R.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : ": " + detail)}");
        if (!ok) _failed++;
        try { File.WriteAllText(Path.Combine(Dir!, "report.txt"), R.ToString()); } catch { /* the final write below */ }
    }

    public static async Task RunAsync()
    {
        Active = true;
        R.AppendLine($"pxd Space Analyzer {typeof(SelfTest).Assembly.GetName().Version?.ToString(3)} self-test, {DateTime.Now:yyyy-MM-dd HH:mm}");
        R.AppendLine($"Elevated: {Elevation.IsElevated}");
        R.AppendLine();
        // Everything the window would delete stays inside DIR, and every "are you sure?" is answered here.
        MainWindow.RecycleAction = p => Places.Inside(p, Dir!) && Native.ShellDelete(p, toRecycleBin: false);
        Confirm? asked = null;
        MainWindow.Ask = (_, c) =>
        {
            asked = c;
            return Task.FromResult(true);
        };
        try
        {
            if (Sample)
            {
                await Screenshots();
            }
            else
            {
                Units();
                var scan = ScanChecks();
                TreemapChecks(scan);
                RealScan();
                await WasteChecks(() => asked);
                await WindowChecks(scan, () => asked);
                await Placement();
                SingleInstanceCheck();
                await UpdateChecks();
            }
        }
        catch (Exception ex)
        {
            Check("no crash", false, ex.ToString());
        }

        R.AppendLine();
        R.AppendLine(_failed == 0 ? "RESULT: all checks passed" : $"RESULT: {_failed} check(s) FAILED");
        ExitCode = _failed == 0 ? 0 : 1;
        await File.WriteAllTextAsync(Path.Combine(Dir!, "report.txt"), R.ToString());
    }

    // ---------- Small pieces ----------

    private static void Units()
    {
        R.AppendLine("== Units ==");
        Check("Sizes", Text.Size(0) == "0 B" && Text.Size(1536) == "1.50 KB" && Text.Size(10L << 30) == "10.0 GB" && Text.Size(500L << 30) == "500 GB",
            $"{Text.Size(1536)} {Text.Size(10L << 30)} {Text.Size(500L << 30)}");
        Check("Cluster rounding", Scanner.Round(1, 4096) == 4096 && Scanner.Round(4096, 4096) == 4096 && Scanner.Round(4097, 4096) == 8192 && Scanner.Round(0, 4096) == 0);
        Check("File kinds", FileTypes.OfName("movie.MKV") == FileTypes.Video && FileTypes.OfName("a.dll") == FileTypes.Program && FileTypes.OfName("noext") == FileTypes.Other);

        double[] w = [6, 6, 4, 3, 2, 2, 1];
        var area = new Box(0, 0, 600, 400);
        var boxes = Treemap.Squarify(w, area);
        var scale = area.Area / w.Sum();
        var areasOk = boxes.Select((b, i) => Math.Abs(b.Area - w[i] * scale) < 0.01).All(x => x);
        var inside = boxes.All(b => b.X >= -0.001 && b.Y >= -0.001 && b.X + b.W <= 600.001 && b.Y + b.H <= 400.001);
        var overlap = false;
        for (var i = 0; i < boxes.Length; i++)
        for (var j = i + 1; j < boxes.Length; j++)
        {
            var a = boxes[i];
            var b = boxes[j];
            if (a.X + a.W - 0.001 > b.X && b.X + b.W - 0.001 > a.X && a.Y + a.H - 0.001 > b.Y && b.Y + b.H - 0.001 > a.Y) overlap = true;
        }

        Check("Treemap: each box has its share of the area", areasOk);
        Check("Treemap: boxes fill the area without overlapping", inside && !overlap);
        Check("Treemap: squarified (worst aspect ratio under 3, the classic example)", Treemap.WorstAspect(boxes) < 3, Treemap.WorstAspect(boxes).ToString("0.00"));
        Check("Treemap: zero and empty input", Treemap.Squarify([0, 0], area).All(b => b.Area == 0) && Treemap.Squarify([], area).Length == 0);

        var dism = """
                   Deployment Image Servicing and Management tool
                   Component Store (WinSxS) information:

                   Windows Explorer Reported Size of Component Store : 9.71 GB

                   Actual Size of Component Store : 9.52 GB

                       Shared with Windows : 6.14 GB
                       Backups and Disabled Features : 3.10 GB
                       Cache and Temporary Data :  281.43 MB

                   Date of Last Cleanup : 2026-10-01 03:12:44

                   Number of Reclaimable Packages : 4
                   Component Store Cleanup Recommended : Yes
                   """;
        var s = WasteFinder.ParseDism(dism);
        Check("DISM component store report is read", s is { Reclaimable: 4, CleanupRecommended: true } && Math.Abs(s.ActualSize - 9.52 * (1L << 30)) < 1 << 20 && s.CacheAndTemp > 280L << 20,
            s?.ToString());
        Check("DISM: nothing read from other text", WasteFinder.ParseDism("Error: 740 Elevated permissions are required") is null);
        R.AppendLine();
    }

    // ---------- Scanning a made-up tree ----------

    private static string Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o.Trim();
    }

    private static void Write(string path, long size, DateTime? when = null)
    {
        var full = Native.Long(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var f = new FileStream(full, FileMode.Create, FileAccess.Write))
        {
            if (size > 0)
            {
                var buf = new byte[Math.Min(size, 1 << 20)];
                Random.Shared.NextBytes(buf);   // not sparse, not compressible
                for (long left = size; left > 0; left -= buf.Length) f.Write(buf, 0, (int)Math.Min(left, buf.Length));
            }
        }

        if (when is { } t)
        {
            File.SetLastWriteTime(full, t);
            File.SetCreationTime(full, t);
        }
    }

    private static ScanResult ScanChecks()
    {
        R.AppendLine("== Scan (made-up tree) ==");
        var root = Path.Combine(Dir!, "tree");
        Unlock(Path.Combine(root, "locked"));
        Fresh(root);
        var outside = Path.Combine(Dir!, "outside");
        Directory.CreateDirectory(outside);
        Write(Path.Combine(outside, "big-outside.bin"), 8L << 20);

        var cluster = Native.ClusterSize(Dir!);
        long size = 0, alloc = 0, files = 0;
        void W(string rel, long s)
        {
            Write(Path.Combine(root, rel), s);
            size += s;
            alloc += Scanner.Round(s, cluster);
            files++;
        }

        W(@"a\one.txt", 1000);
        W(@"a\two.log", 5000);
        W(@"a\three.mp4", 1 << 20);
        W(@"a\deep\x\y\z\movie.mkv", 3L << 20);
        W(@"a\deep\empty.txt", 0);
        for (var i = 0; i < 100; i++) W($@"b\small{i:000}.dat", 100 + i);
        W(@"b\huge.iso", 20L << 20);
        // A path longer than 260 characters.
        var longRel = string.Join("\\", Enumerable.Range(0, 14).Select(i => $"long-folder-name-{i:00}")) + @"\far.txt";
        W(Path.Combine("c", longRel), 777);
        var link = Path.Combine(root, "link-to-outside");
        var mk = Junction(link, outside);
        Check("Test junction made", Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), mk);
        // A folder this account isn't allowed to list.
        var locked = Path.Combine(root, "locked");
        W(@"locked\secret.bin", 4096);
        var lockedOk = Lock(locked);


        var watch = Stopwatch.StartNew();
        var r = Scanner.Scan(root);
        watch.Stop();
        Unlock(locked);
        var lockedSize = 4096L;
        var lockedAlloc = Scanner.Round(4096, cluster);
        Check("Scan: total size", r.Root.Size == size - (lockedOk ? lockedSize : 0), $"{r.Root.Size} vs {size}");
        Check("Scan: total on disk (cluster " + cluster + ")", r.Root.Alloc == alloc - (lockedOk ? lockedAlloc : 0), $"{r.Root.Alloc} vs {alloc}");
        Check("Scan: file count", r.Root.FileCount == files - (lockedOk ? 1 : 0), $"{r.Root.FileCount} vs {files}");
        var linkNode = r.Root.Dirs.FirstOrDefault(d => d.Name == "link-to-outside");
        Check("Scan: junction shown, not followed", linkNode is { IsLink: true, FileCount: 0, Alloc: 0 } && r.LinksSkipped == 1 && !r.TopFiles.Any(f => f.Name == "big-outside.bin"));
        Check("Scan: a folder it can't read is counted quietly", !lockedOk || (r.UnreadableCount == 1 && r.Root.Dirs.First(d => d.Name == "locked").Unreadable), lockedOk ? r.UnreadableCount.ToString() : "couldn't lock the test folder; skipped");
        Check("Scan: long path read", r.TopFiles.Any(f => f.Name == "far.txt" && f.Path.Length > 260) || r.Root.Find(Path.Combine(root, "c"))?.FileCount == 1);
        var b = r.Root.Find(Path.Combine(root, "b"));
        Check("Scan: a folder with many files keeps the biggest by name, counts the rest", b is { OwnFiles: 101 } && b.Files.Length == Scanner.KeepPerFolder && b.SmallCount == 101 - Scanner.KeepPerFolder && b.Files[0].Name == "huge.iso",
            $"{b?.Files.Length} kept, {b?.SmallCount} counted");
        Check("Scan: biggest file first", r.TopFiles.FirstOrDefault()?.Name == "huge.iso" && r.TopFiles.Count == Math.Min(Scanner.TopFileCount, files - (lockedOk ? 1 : 0)), $"{r.TopFiles.Count} listed");
        Check("Scan: file types", r.Extensions.First().Ext == ".iso" && r.Extensions.First(e => e.Ext == ".dat").Count == 100);
        Check("Scan: folders sorted biggest first", r.Root.Dirs.First().Name == "b");
        Check("Scan: path of a deep folder", r.Root.Find(Path.Combine(root, @"a\deep\x\y"))?.FullPath == Path.Combine(root, @"a\deep\x\y"));
        Check("Scan: last modified rolls up", r.Root.LastWrite > DateTime.Now.AddMinutes(-10));
        using (var c = new CancellationTokenSource())
        {
            c.Cancel();
            var stopped = Scanner.Scan(root, cancel: c.Token);
            Check("Scan: cancel stops it", stopped.Cancelled);
        }

        R.AppendLine($"  made-up tree: {r.Root.FileCount} files, {r.Root.DirCount} folders in {watch.ElapsedMilliseconds} ms");
        R.AppendLine();
        return r;
    }

    /// <summary>Clears a folder of an earlier run (its junctions are removed, never followed); one that's still busy is moved aside.</summary>
    private static void Fresh(string dir)
    {
        for (var i = 0; i < 10 && Directory.Exists(dir); i++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                Thread.Sleep(300);
            }
        }

        if (Directory.Exists(dir)) Directory.Move(dir, dir + ".old-" + DateTime.Now.Ticks);
    }

    private static bool Lock(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            info.SetAccessControl(acl);
            try
            {
                Directory.GetFiles(dir);
                return false;   // still readable (an administrator who owns it, say)
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void Unlock(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            acl.RemoveAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            info.SetAccessControl(acl);
        }
        catch
        {
            /* best effort */
        }
    }

    private static void TreemapChecks(ScanResult scan)
    {
        R.AppendLine("== Treemap ==");
        var area = new Box(0, 0, 1000, 600);
        var tiles = Treemap.Layout(scan.Root, area, minSide: 2);
        var top = tiles.Where(t => t.Depth == 1).ToList();
        var sum = top.Sum(t => t.Box.Area);
        Check("Layout: the top level fills the map", Math.Abs(sum - area.Area) < area.Area * 0.001, $"{sum:0} of {area.Area:0}");
        Check("Layout: nothing outside the map", tiles.All(t => t.Box.X >= -0.01 && t.Box.Y >= -0.01 && t.Box.X + t.Box.W <= 1000.01 && t.Box.Y + t.Box.H <= 600.01));
        var b = top.FirstOrDefault(t => t.Name == "b");
        Check("Layout: folder b takes its share", b is not null && Math.Abs(b.Box.Area / area.Area - (double)b.Size / scan.Root.Alloc) < 0.001);
        var children = tiles.Where(t => t.Depth == 2 && ReferenceEquals(t.Owner, b?.Folder)).ToList();
        Check("Layout: files nested inside their folder", children.Count > 0 && children.All(c => c.Box.X >= b!.Box.X - 0.01 && c.Box.Y >= b.Box.Y - 0.01 && c.Box.X + c.Box.W <= b.Box.X + b.Box.W + 0.01), $"{children.Count} tiles in b");
        Check("Layout: files coloured by kind", tiles.Any(t => t.Name == "huge.iso" && t.Kind == FileTypes.Archive) && tiles.Any(t => t.Name.EndsWith("smaller files") && t.Kind == FileTypes.Folder));
        R.AppendLine();
    }

    // ---------- A real read-only scan ----------

    private static void RealScan()
    {
        R.AppendLine("== Real scan (read only) ==");
        var drivers = Path.Combine(Environment.SystemDirectory, "drivers");
        var r = Scanner.Scan(drivers);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        long netFiles = 0, netSize = 0;
        foreach (var f in new DirectoryInfo(drivers).EnumerateFiles("*", opts))
        {
            netFiles++;
            netSize += f.Length;
        }

        Check($"{drivers}: same files and size as .NET's own listing", r.Root.FileCount == netFiles && r.Root.Size == netSize,
            $"{r.Root.FileCount} files, {Text.Size(r.Root.Size)} (.NET {netFiles}, {Text.Size(netSize)}), {r.Elapsed.TotalMilliseconds:0} ms");

        var sys = Environment.SystemDirectory;
        var w1 = Stopwatch.StartNew();
        var big = Scanner.Scan(sys);
        w1.Stop();
        var w2 = Stopwatch.StartNew();
        long n = 0;
        foreach (var _ in new DirectoryInfo(sys).EnumerateFiles("*", opts)) n++;
        w2.Stop();
        var rate = big.Root.FileCount / Math.Max(0.001, w1.Elapsed.TotalSeconds);
        Check($"{sys}: scanned fast", big.Root.FileCount > 1000 && w1.Elapsed.TotalSeconds < 30,
            $"{Text.Count(big.Root.FileCount)} files, {Text.Count(big.Root.DirCount)} folders, {Text.Size(big.Root.Alloc)} on disk in {w1.Elapsed.TotalSeconds:0.00} s ({rate:N0} files/s; .NET's single-threaded listing: {w2.Elapsed.TotalSeconds:0.00} s for {n:N0}); {big.UnreadableCount} unreadable");
        R.AppendLine();
    }

    // ---------- The usual waste on a made-up PC ----------

    private static string Pc => Path.Combine(Dir!, "pc");

    private static void BuildPc()
    {
        var root = Pc;
        Fresh(root);
        var old = DateTime.Now.AddDays(-3);
        var u = Path.Combine(root, @"Users\alex");
        var local = Path.Combine(u, @"AppData\Local");
        Write(Path.Combine(local, @"Temp\old1.tmp"), 300_000, old);
        Write(Path.Combine(local, @"Temp\sub\old2.tmp"), 200_000, old);
        Directory.SetCreationTime(Path.Combine(local, @"Temp\sub"), old);
        Directory.CreateDirectory(Path.Combine(local, @"Temp\oldempty"));
        Directory.SetCreationTime(Path.Combine(local, @"Temp\oldempty"), old);
        Write(Path.Combine(local, @"Temp\new.tmp"), 50_000);
        // A junction inside temp that points at something precious: it must survive the clean-up.
        Write(Path.Combine(root, @"precious\keep.docx"), 10_000, old);
        Junction(Path.Combine(local, @"Temp\link-to-precious"), Path.Combine(root, "precious"));
        Write(Path.Combine(root, @"Windows\Temp\old.log"), 100_000, old);
        Write(Path.Combine(root, @"Windows\Temp\new.log"), 100_000);
        Write(Path.Combine(root, @"Windows\SoftwareDistribution\Download\abc\update.cab"), 2_000_000);
        Write(Path.Combine(root, @"Windows\SoftwareDistribution\Download\inuse.cab"), 100_000);
        Write(Path.Combine(root, @"Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache\x\piece.bin"), 1_500_000);
        Write(Path.Combine(root, @"Windows.old\Windows\System32\old.dll"), 700_000);
        Write(Path.Combine(root, @"$Recycle.Bin\S-1-5-21-1-2-3-1001\$RABC123.zip"), 400_000);
        Write(Path.Combine(root, "hiberfil.sys"), 3_000_000);
        Write(Path.Combine(root, "pagefile.sys"), 2_500_000);
        Write(Path.Combine(root, @"Windows\MEMORY.DMP"), 900_000);
        Write(Path.Combine(root, @"Windows\Minidump\111526-1.dmp"), 300_000);
        Write(Path.Combine(root, @"Windows\LiveKernelReports\WATCHDOG\w.dmp"), 600_000);
        Write(Path.Combine(root, @"ProgramData\Microsoft\Windows\WER\ReportQueue\AppCrash_x\Report.wer"), 80_000);
        Write(Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive\AppCrash_y\Report.wer"), 40_000);
        Write(Path.Combine(local, @"CrashDumps\app.exe.1234.dmp"), 250_000);
        Write(Path.Combine(local, @"Microsoft\Edge\User Data\Default\Cache\Cache_Data\f_000001"), 1_200_000);
        Write(Path.Combine(local, @"Microsoft\Edge\User Data\Profile 1\Code Cache\js\a"), 300_000);
        Write(Path.Combine(local, @"Microsoft\Edge\User Data\Default\Bookmarks"), 5_000);   // not a cache
        Write(Path.Combine(local, @"Google\Chrome\User Data\Default\Cache\Cache_Data\f_000002"), 800_000);
        Write(Path.Combine(local, @"Mozilla\Firefox\Profiles\abcd.default-release\cache2\entries\E1"), 600_000);
        Write(Path.Combine(local, @"Microsoft\Windows\Explorer\thumbcache_256.db"), 400_000);
        Write(Path.Combine(local, @"Microsoft\Windows\Explorer\iconcache_16.db"), 50_000);   // not a thumbnail cache
        Write(Path.Combine(root, @"Windows\Installer\$PatchCache$\Managed\x\file.dll"), 1_100_000);
        Write(Path.Combine(root, @"Windows\Installer\1a2b3c.msi"), 2_000_000);
        Write(Path.Combine(u, @"Downloads\setup-old.exe"), 1_700_000);
        Write(Path.Combine(root, @"Windows\WinSxS\amd64_x\component.dll"), 500_000);
    }

    private static async Task WasteChecks(Func<Confirm?> asked)
    {
        R.AppendLine("== The usual waste (made-up PC inside the test folder) ==");
        BuildPc();
        var root = Pc;
        var local = Path.Combine(root, @"Users\alex\AppData\Local");
        var places = Places.Test(root, new HashSet<string> { "chrome" });   // Chrome is "open"
        Check("Made-up PC: one profile, its own Windows and drive", places.Profiles.Count == 1 && places.Profiles[0].Name == "alex" && places.DriveRoots.Count == 1 && Places.Inside(places.Windows, Dir!));
        var items = WasteFinder.Find(places);
        WasteItem? I(string id) => items.FirstOrDefault(i => i.Id == id);
        string Sz(string id) => I(id) is { } x ? Text.Size(x.Size) : "missing";
        var cl = Native.ClusterSize(root);
        long Al(params long[] s) => s.Sum(x => Scanner.Round(x, cl));
        Check("Temp: only files older than a day", I("usertemp")?.Size == Al(300_000, 200_000) && I("usertemp")?.Files == 2, Sz("usertemp"));
        Check("Windows temp", I("wintemp")?.Size == Al(100_000), Sz("wintemp"));
        Check("Windows Update downloads", I("wu")?.Size == Al(2_000_000, 100_000), Sz("wu"));
        Check("Delivery Optimization", I("do")?.Size == Al(1_500_000), Sz("do"));
        Check("Windows.old: report only", I("winold") is { How: CleanHow.None, CanClean: false } wo && wo.Size == Al(700_000), Sz("winold"));
        Check("Recycle Bin", I("recycle")?.Size == Al(400_000) && I("recycle")!.CanClean, Sz("recycle"));
        Check("hiberfil.sys: report only, powercfg note", I("hiber") is { How: CleanHow.None } h && h.Size == Al(3_000_000) && h.Note.Contains("powercfg -h off"), Sz("hiber"));
        Check("Page file: report only", I("pagefile") is { How: CleanHow.None } pf && pf.Size == Al(2_500_000), Sz("pagefile"));
        Check("Crash dumps: MEMORY.DMP, Minidump, LiveKernelReports", I("dumps")?.Size == Al(900_000, 300_000, 600_000) && I("dumps")!.How == CleanHow.RecycleBin, Sz("dumps"));
        Check("App crash dumps", I("appdumps")?.Size == Al(250_000), Sz("appdumps"));
        Check("Error reports (Windows and user)", I("wer")?.Size == Al(80_000) && I("userwer")?.Size == Al(40_000));
        Check("Edge cache: every profile, not bookmarks", I("edge")?.Size == Al(1_200_000, 300_000) && I("edge")!.CanClean, Sz("edge"));
        Check("Chrome cache: blocked while Chrome is open", I("chrome") is { CanClean: false, Blocked: "Close Google Chrome first" } c && c.Size == Al(800_000), I("chrome")?.Blocked);
        Check("Firefox cache", I("firefox")?.Size == Al(600_000), Sz("firefox"));
        Check("Thumbnail cache: thumbcache only", I("thumbs")?.Size == Al(400_000), Sz("thumbs"));
        Check("Installer patch cache: report only", I("patchcache") is { How: CleanHow.None } pc && pc.Size == Al(1_100_000) && pc.Note.Contains("Never delete"), Sz("patchcache"));
        Check("Downloads: report only", I("downloads") is { How: CleanHow.None } dl && dl.Size == Al(1_700_000), Sz("downloads"));
        Check("WinSxS: report only", I("winsxs") is { How: CleanHow.None, CanClean: false });
        Check("Every item has a plain-English note", items.All(i => i.Note.Length > 30));

        // Without administrator rights, Windows' own caches are not cleaned.
        var limited = new Places
        {
            SystemRoot = places.SystemRoot, Windows = places.Windows, ProgramData = places.ProgramData, Profiles = places.Profiles, DriveRoots = places.DriveRoots,
            IsTest = true, Elevated = false, IsRunning = places.IsRunning, QueryRecycleBin = places.QueryRecycleBin, EmptyRecycleBin = places.EmptyRecycleBin,
            Recycle = places.Recycle, ClearDeliveryOptimization = places.ClearDeliveryOptimization,
        };
        var wuLimited = WasteFinder.Items(limited).First(i => i.Id == "wu");
        WasteFinder.Measure(wuLimited, limited);
        var refused = Cleaner.Clean(wuLimited, limited);
        Check("Needs administrator: blocked and nothing deleted", wuLimited.Blocked == "Needs administrator rights" && refused.Deleted == 0 && File.Exists(Path.Combine(root, @"Windows\SoftwareDistribution\Download\abc\update.cab")));

        // Clean everything that can be cleaned, through the window, with one file held open.
        var w = OffScreen(places);
        w.Show();
        w.ShowPage(AppPage.Waste);
        for (var i = 0; i < 200 && (w.WasteBusy || w.Waste.Count == 0); i++) await Task.Delay(25);
        Check("Waste page lists the items", w.Waste.Count == items.Count, $"{w.Waste.Count} items");
        Shot(w, "waste.png");
        foreach (var item in w.Waste) w.PickWaste(item, true);
        Check("Only cleanable items can be picked", w.PickedWaste.All(i => i.CanClean) && !w.PickedWaste.Any(i => i.Id is "chrome" or "winold" or "hiber" or "patchcache" or "downloads" or "winsxs"));
        List<(WasteItem Item, CleanResult Result)> results;
        using (new FileStream(Path.Combine(root, @"Windows\SoftwareDistribution\Download\inuse.cab"), FileMode.Open, FileAccess.Read, FileShare.None))
            results = await w.CleanPickedAsync();
        var c2 = asked();
        Check("Confirm lists each item and says how it goes", c2 is not null && c2.Lines.Count == results.Count && c2.Lines.Any(l => l.Contains("deleted permanently")) && c2.Lines.Any(l => l.Contains("Recycle Bin")) && c2.Message.Contains("permanently"),
            c2 is null ? "not asked" : string.Join(" | ", c2.Lines));
        bool Gone(string rel) => !File.Exists(Path.Combine(root, rel));
        bool Kept(string rel) => File.Exists(Path.Combine(root, rel));
        var lu = @"Users\alex\AppData\Local\";
        Check("Temp: old files gone, new one kept, old empty folder gone", Gone(lu + @"Temp\old1.tmp") && Gone(lu + @"Temp\sub\old2.tmp") && Kept(lu + @"Temp\new.tmp") && !Directory.Exists(Path.Combine(local, @"Temp\oldempty")));
        Check("Temp: the junction and what it points to are untouched", Kept(@"precious\keep.docx") && Directory.Exists(Path.Combine(local, @"Temp\link-to-precious")));
        Check("Windows temp: old gone, new kept", Gone(@"Windows\Temp\old.log") && Kept(@"Windows\Temp\new.log"));
        var wu = results.FirstOrDefault(x => x.Item.Id == "wu").Result;
        Check("Update downloads: cleaned, the file in use skipped", Gone(@"Windows\SoftwareDistribution\Download\abc\update.cab") && Kept(@"Windows\SoftwareDistribution\Download\inuse.cab") && wu?.Skipped == 1, wu?.Message);
        Check("Delivery Optimization cleaned", Gone(@"Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache\x\piece.bin"));
        Check("Recycle Bin emptied", Gone(@"$Recycle.Bin\S-1-5-21-1-2-3-1001\$RABC123.zip"));
        Check("Crash dumps removed", Gone(@"Windows\MEMORY.DMP") && Gone(@"Windows\Minidump\111526-1.dmp") && Gone(@"Windows\LiveKernelReports\WATCHDOG\w.dmp") && Gone(lu + @"CrashDumps\app.exe.1234.dmp"));
        Check("Error reports removed", Gone(@"ProgramData\Microsoft\Windows\WER\ReportQueue\AppCrash_x\Report.wer") && Gone(lu + @"Microsoft\Windows\WER\ReportArchive\AppCrash_y\Report.wer"));
        Check("Edge cache removed, bookmarks kept", Gone(lu + @"Microsoft\Edge\User Data\Default\Cache\Cache_Data\f_000001") && Gone(lu + @"Microsoft\Edge\User Data\Profile 1\Code Cache\js\a") && Kept(lu + @"Microsoft\Edge\User Data\Default\Bookmarks"));
        Check("Chrome cache kept (Chrome open)", Kept(lu + @"Google\Chrome\User Data\Default\Cache\Cache_Data\f_000002"));
        Check("Firefox cache removed", Gone(lu + @"Mozilla\Firefox\Profiles\abcd.default-release\cache2\entries\E1"));
        Check("Thumbnail cache removed, icon cache kept", Gone(lu + @"Microsoft\Windows\Explorer\thumbcache_256.db") && Kept(lu + @"Microsoft\Windows\Explorer\iconcache_16.db"));
        Check("Report-only items untouched (Installer, Windows.old, hiberfil, pagefile, Downloads, WinSxS)",
            Kept(@"Windows\Installer\1a2b3c.msi") && Kept(@"Windows\Installer\$PatchCache$\Managed\x\file.dll") && Kept(@"Windows.old\Windows\System32\old.dll") && Kept("hiberfil.sys") && Kept("pagefile.sys")
            && Kept(@"Users\alex\Downloads\setup-old.exe") && Kept(@"Windows\WinSxS\amd64_x\component.dll"));
        var expectFreed = Al(300_000, 200_000, 100_000, 2_000_000, 1_500_000, 400_000, 900_000, 300_000, 600_000, 250_000, 80_000, 40_000, 1_200_000, 300_000, 600_000, 400_000);
        var freed = results.Sum(x => x.Result.Freed);
        Check("Freed what was promised", freed == expectFreed, $"{Text.Size(freed)} of {Text.Size(expectFreed)}");
        Check("Measured again afterwards", w.Waste.First(i => i.Id == "edge").Size == 0 && w.Waste.First(i => i.Id == "wu").Size == Al(100_000));
        Check("Result shown", w.NoticeShowing && w.NoticeText.StartsWith("Freed"), w.NoticeText);
        Shot(w, "waste-after.png");
        w.Close();

        // Browser opened after the list was made: the last check before deleting catches it.
        var late = WasteFinder.Items(places).First(i => i.Id == "edge");
        Write(Path.Combine(local, @"Microsoft\Edge\User Data\Default\Cache\Cache_Data\f_000009"), 100_000);
        WasteFinder.Measure(late, places);
        var opened = new Places
        {
            SystemRoot = places.SystemRoot, Windows = places.Windows, ProgramData = places.ProgramData, Profiles = places.Profiles, DriveRoots = places.DriveRoots,
            IsTest = true, Elevated = true, IsRunning = n => n == "msedge",
        };
        var lateResult = Cleaner.Clean(late, opened);
        Check("Browser opened in the meantime: nothing deleted", lateResult.Deleted == 0 && lateResult.Message!.Contains("is open") && File.Exists(Path.Combine(local, @"Microsoft\Edge\User Data\Default\Cache\Cache_Data\f_000009")));
        R.AppendLine();
    }

    // ---------- The window ----------

    private static MainWindow OffScreen(Places? places = null) => new(places ?? Places.Test(Pc))
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Position = new PixelPoint(-30000, -30000),
        ShowActivated = false,
        ShowInTaskbar = false,
        SelfTestMode = true,
    };

    private static void Shot(Window w, string file)
    {
        w.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var width = (int)(double.IsNaN(w.Width) ? w.Bounds.Width : w.Width);
        var height = (int)(double.IsNaN(w.Height) ? w.Bounds.Height : w.Height);
        using var bmp = new RenderTargetBitmap(new PixelSize(Math.Max(1, width), Math.Max(1, height)));
        bmp.Render(w);
        var dir = Sample ? Path.Combine(Dir!, "screenshots") : Dir!;
        Directory.CreateDirectory(dir);
        bmp.Save(Path.Combine(dir, file), PngBitmapEncoderOptions.Default);
    }

    private static async Task WindowChecks(ScanResult scan, Func<Confirm?> asked)
    {
        R.AppendLine("== Window ==");
        MainWindow.DriveSource = SampleDrives;
        var w = OffScreen();
        w.Show();
        await Task.Delay(50);
        Check("Opens on the start page with the drives", w.CurrentPage == AppPage.Home);
        Shot(w, "home.png");

        // A real scan of the made-up tree through the window.
        var root = Path.Combine(Dir!, "tree");
        await w.ScanAsync(root);
        var expected = Scanner.Scan(root).Root.FileCount;   // the locked folder is readable again now
        Check("Scan from the window", w.Result is { } r && r.Root.FileCount == expected && w.CurrentPage == AppPage.Folders, $"{w.Result?.Root.FileCount} files");
        w.SetTreeSort(2, descending: true);
        Check("Tree shows the root opened, biggest first", w.TreeRows.Count > 1 && w.TreeRows[1].Name == "b", string.Join(", ", w.TreeRows.Take(5).Select(x => x.Name)));
        var a = w.Result!.Root.Dirs.First(d => d.Name == "a");
        w.Toggle(a);
        Check("Opening a folder shows its contents", w.TreeRows.Any(x => x.Owner == a && x.Name == "three.mp4"));
        w.SetTreeSort(0, descending: false);
        Check("Sort by name", w.TreeRows.Where(x => x.Depth == 1).Select(x => x.Name).SequenceEqual(w.TreeRows.Where(x => x.Depth == 1).Select(x => x.Name).Order(StringComparer.CurrentCultureIgnoreCase)));
        w.SetTreeSort(2, descending: true);
        Shot(w, "folders.png");

        // Delete a folder from the tree (inside the test folder; the self-test's delete is permanent, not the Recycle Bin).
        var deep = w.Result.Root.Find(Path.Combine(root, @"a\deep"))!;
        var before = w.Result.Root.Alloc;
        var deepAlloc = deep.Alloc;
        w.Reveal(deep);
        Check("Reveal selects the folder", w.SelectedRow?.Folder == deep);
        var deleted = await w.DeleteSelectedAsync();
        Check("Delete to Recycle Bin asks first, then deletes", deleted && asked()?.Yes == "Move to Recycle Bin" && !Directory.Exists(Path.Combine(root, @"a\deep")));
        Check("Tree totals drop by what was deleted", w.Result.Root.Alloc == before - deepAlloc && w.Result.Root.Find(Path.Combine(root, @"a\deep")) is null);
        Check("Drive roots, the scanned folder, Windows and profiles are protected", w.IsProtected(@"C:\") && w.IsProtected(root) && w.IsProtected(Path.Combine(Pc, "Windows", "System32"))
                                                                                     && w.IsProtected(Path.Combine(Pc, @"Users\alex")) && !w.IsProtected(Path.Combine(root, "b")));

        w.ShowPage(AppPage.Treemap);
        await Task.Delay(50);
        Shot(w, "treemap.png");
        Check("Treemap drawn", w.Map.Tiles.Count > 10, $"{w.Map.Tiles.Count} tiles");
        var bTile = w.Map.Tiles.First(t => t.Depth == 1 && t.Name == "b");
        var inside = new Point(bTile.Box.X + bTile.Box.W / 2, bTile.Box.Y + bTile.Box.H / 2);
        Check("Click inside a folder zooms into it", w.Map.FolderAt(inside)?.Name == "b");
        w.ZoomMap(w.Map.FolderAt(inside)!);
        Check("Zoomed", w.MapRoot?.Name == "b" && w.Map.Tiles.Any(t => t.Name == "huge.iso"));
        w.ZoomMap(w.MapRoot!.Parent!);

        w.ShowPage(AppPage.Files);
        await Task.Delay(30);
        Check("Biggest files listed", w.FilesShown == w.Result.TopFiles.Count && w.FilesShown > 50);
        Shot(w, "files.png");
        w.ShowPage(AppPage.Types);
        await Task.Delay(30);
        Check("File types listed", w.TypesShown == w.Result.Extensions.Count);
        Shot(w, "types.png");

        // Unreadable folders: the notice.
        w.SetResult(new ScanResult { Root = w.Result.Root, TopFiles = w.Result.TopFiles, Extensions = w.Result.Extensions, UnreadableCount = 3, Elapsed = TimeSpan.FromSeconds(1) });
        Check("Says how many folders couldn't be read", w.NoticeShowing && w.NoticeText.StartsWith("3 folders couldn't be read"), w.NoticeText);

        // The confirm dialog itself, rendered off-screen.
        var dialog = new ConfirmDialog(new Confirm("Clean up?", "2.10 GB in 3 items. Caches and temp files are deleted permanently (they don't go to the Recycle Bin). Files in use are skipped.",
            ["Your temp files: 1.20 GB, deleted permanently (not to the Recycle Bin)", "Windows Update downloads: 850 MB, deleted permanently (not to the Recycle Bin)", "Crash dumps (Windows): 64 MB, moved to the Recycle Bin"], "Clean up"))
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-30000, -30000), ShowActivated = false,
        };
        dialog.Show();
        await Task.Delay(50);
        Check("Confirm dialog has the pxd title bar", dialog.Content is DockPanel && !dialog.CanResize);
        Shot(dialog, "confirm.png");
        dialog.Close();
        w.Close();
        MainWindow.DriveSource = DriveSpace.Fixed;
        Check("Real drives listed with sizes", DriveSpace.Fixed() is { Count: > 0 } d && d.All(x => x.Total > 0 && x.Free <= x.Total), string.Join(", ", DriveSpace.Fixed().Select(x => x.Root)));
        R.AppendLine();
    }

    private static List<DriveSpace> SampleDrives() =>
    [
        new(@"C:\", "Windows", "NTFS", 1_000_068_870_144, 212_400_000_000),
        new(@"D:\", "Games", "NTFS", 2_000_381_014_016, 151_000_000_000),
        new(@"E:\", "Backup", "NTFS", 4_000_768_323_584, 2_870_000_000_000),
    ];

    private static async Task Placement()
    {
        R.AppendLine("== Window placement ==");
        const string key = "selftest";
        var file = Path.Combine(Program.DataFolder, "windows.json");
        if (File.Exists(file)) File.Delete(file);
        var a = OffScreen();
        pxdStandard.pxdChrome.RememberPlacement(a, key);
        a.Show();
        await Task.Delay(50);
        a.Width = 1100;
        a.Height = 700;
        a.Position = new PixelPoint(-29000, -29000);
        a.UpdateLayout();
        await Task.Delay(50);
        a.Close();
        var json = File.Exists(file) ? await File.ReadAllTextAsync(file) : "";
        Check("Placement saved", json.Contains("\"selftest\"") && json.Contains("\"Width\":1100") && json.Contains("\"Height\":700"), json);
        var b = OffScreen();
        pxdStandard.pxdChrome.RememberPlacement(b, key);
        Check("Placement restored (size), off-screen spot ignored", b.Width == 1100 && b.Height == 700 && b.Position == new PixelPoint(-30000, -30000), $"{b.Width}x{b.Height} at {b.Position}");
        R.AppendLine();
    }

    private static void SingleInstanceCheck()
    {
        R.AppendLine("== Single instance ==");
        var name = "pxdSpaceAnalyzer-selftest-" + Guid.NewGuid().ToString("N")[..8];
        string[]? got = null;
        using var done = new ManualResetEventSlim();
        using (SingleInstance.Listen(name, args => { got = args; done.Set(); }))
        {
            var sent = SingleInstance.Send(name, ["--scan", @"D:\"]);
            done.Wait(3000);
            Check("Second start reaches the running copy", sent && got is ["--scan", @"D:\"], got is null ? "nothing received" : string.Join("|", got));
        }

        R.AppendLine();
    }

    private static async Task UpdateChecks()
    {
        R.AppendLine("== Update before start (local fake release, temp folders) ==");
        foreach (var (name, ok, detail) in await SelfTestUpdate.RunAsync(Dir!))
            Check(name, ok, detail.Length > 0 ? detail : null);
        R.AppendLine();
    }

    // ---------- README screenshots (made-up data, nothing measured on this PC) ----------

    private static async Task Screenshots()
    {
        R.AppendLine("== Screenshots (made-up data) ==");
        MainWindow.DriveSource = SampleDrives;
        var w = OffScreen(Places.Test(Path.Combine(Dir!, "empty-pc")));
        w.Show();
        await Task.Delay(50);
        Shot(w, "home.png");
        var result = SampleTree();
        w.SetResult(result);
        var users = result.Root.Dirs.First(d => d.Name == "Users");
        w.Toggle(users);
        var alex = users.Dirs.First(d => d.Name == "alex");
        w.Toggle(alex);
        w.Toggle(alex.Dirs.First(d => d.Name == "AppData"));
        w.SelectRow(new TreeRow { Owner = alex, Folder = alex.Dirs.First(d => d.Name == "Videos"), Depth = 3 });
        await Task.Delay(50);
        Shot(w, "folders.png");
        w.ShowPage(AppPage.Treemap);
        await Task.Delay(50);
        Shot(w, "treemap.png");
        w.ShowPage(AppPage.Files);
        await Task.Delay(30);
        Shot(w, "files.png");
        w.ShowPage(AppPage.Types);
        await Task.Delay(30);
        Shot(w, "types.png");
        w.SetWaste(SampleWaste());
        foreach (var i in w.Waste.Where(i => i.Id is "usertemp" or "wu" or "edge" or "recycle")) w.PickWaste(i, true);
        w.RefreshWaste();
        w.ShowPage(AppPage.Waste);
        await Task.Delay(30);
        Shot(w, "waste.png");
        w.Close();
        var dialog = new ConfirmDialog(new Confirm("Clean up?", "12.6 GB in 4 items. Caches, temp files and the Recycle Bin are deleted permanently (they don't go to the Recycle Bin). Files in use are skipped.",
            ["Windows Update downloads: 6.12 GB, deleted permanently (not to the Recycle Bin)", "Recycle Bin on C:: 3.40 GB, removed from the Recycle Bin for good",
             "Your temp files: 2.31 GB, deleted permanently (not to the Recycle Bin)", "Microsoft Edge cache: 812 MB, deleted permanently (not to the Recycle Bin)"], "Clean up"))
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-30000, -30000), ShowActivated = false,
        };
        dialog.Show();
        await Task.Delay(50);
        Shot(dialog, "confirm.png");
        dialog.Close();
        MainWindow.DriveSource = DriveSpace.Fixed;
        Check("Screenshots written", Directory.GetFiles(Path.Combine(Dir!, "screenshots"), "*.png").Length >= 7);
    }

    /// <summary>A made-up C: drive: folders, files and their sizes are invented for the screenshots.</summary>
    private static ScanResult SampleTree()
    {
        var rnd = new Random(7);
        var root = DirNode.Root(@"C:\");
        var now = DateTime.Now;
        DirNode D(DirNode parent, string name)
        {
            var d = new DirNode(name, parent);
            parent.Dirs.Add(d);
            return d;
        }

        void F(DirNode d, params (string Name, long Size)[] files)
        {
            var list = d.Files.ToList();
            foreach (var (n, s) in files)
            {
                var ft = now.AddDays(-rnd.Next(0, 400)).AddMinutes(-rnd.Next(0, 1440)).ToFileTime();
                list.Add(new FileEntry(n, s, Scanner.Round(s, 4096), ft));
                d.OwnFiles++;
                d.OwnSize += s;
                d.OwnAlloc += Scanner.Round(s, 4096);
                d.OwnLastWrite = Math.Max(d.OwnLastWrite, ft);
            }

            d.Files = list.OrderByDescending(f => f.Alloc).ToArray();
        }

        void Many(DirNode d, string prefix, string ext, int count, long avg)
        {
            for (var i = 0; i < count; i++) F(d, ($"{prefix}{i:000}{ext}", (long)(avg * (0.3 + rnd.NextDouble() * 1.4))));
        }

        void Small(DirNode d, int count, long total)
        {
            d.SmallCount += count;
            d.SmallSize += total;
            d.SmallAlloc += Scanner.Round(total, 4096) + count * 2048L;
            d.OwnFiles += count;
            d.OwnSize += total;
            d.OwnAlloc += Scanner.Round(total, 4096) + count * 2048L;
            d.OwnLastWrite = Math.Max(d.OwnLastWrite, now.AddDays(-rnd.Next(0, 60)).ToFileTime());
        }

        const long MB = 1 << 20, GB = 1L << 30;
        var users = D(root, "Users");
        var alex = D(users, "alex");
        var videos = D(alex, "Videos");
        var caps = D(videos, "Captures");
        Many(caps, "Game clip ", ".mp4", 18, 900 * MB);
        F(videos, ("Holiday 2025.mkv", (long)(7.4 * GB)), ("Wedding edit final.mov", (long)(4.2 * GB)));
        var pics = D(alex, "Pictures");
        Many(D(pics, "Camera Roll"), "IMG_", ".heic", 60, 3 * MB);
        Small(pics, 4200, 9 * GB);
        var docs = D(alex, "Documents");
        F(docs, ("Tax return 2025.pdf", 4 * MB), ("Budget.xlsx", 2 * MB));
        Small(docs, 1800, (long)(2.6 * GB));
        var dl = D(alex, "Downloads");
        F(dl, ("ubuntu-24.04-desktop-amd64.iso", (long)(5.8 * GB)), ("GameInstaller.exe", 1200 * MB), ("drivers-old.zip", 640 * MB), ("setup.msi", 180 * MB));
        Small(dl, 340, (long)(1.1 * GB));
        var appData = D(alex, "AppData");
        var adLocal = D(appData, "Local");
        var temp = D(adLocal, "Temp");
        Small(temp, 9100, (long)(2.4 * GB));
        var edge = D(D(D(D(adLocal, "Microsoft"), "Edge"), "User Data"), "Default");
        Small(D(edge, "Cache"), 3100, 820 * MB);
        Small(D(D(adLocal, "Packages"), "Microsoft.WindowsStore_8wekyb3d8bbwe"), 900, 310 * MB);
        var roaming = D(appData, "Roaming");
        Small(D(roaming, "discord"), 1400, 640 * MB);
        Small(D(roaming, "Code"), 5200, (long)(1.3 * GB));
        D(alex, "OneDrive");
        var pf = D(root, "Program Files");
        var steam = D(D(pf, "Steam"), "steamapps");
        var common = D(steam, "common");
        var game1 = D(common, "Starfield Odyssey");
        Many(D(game1, "Data"), "Textures", ".ba2", 14, (long)(3.1 * GB));
        var game2 = D(common, "Racing League 25");
        Many(game2, "chunk", ".pak", 22, (long)(1.6 * GB));
        var game3 = D(common, "Pixel Farm");
        Many(game3, "assets", ".bundle", 8, 210 * MB);
        Small(D(pf, "NVIDIA Corporation"), 2400, (long)(2.1 * GB));
        Small(D(pf, "Microsoft Office"), 9800, (long)(4.3 * GB));
        Small(D(pf, "Adobe"), 6100, (long)(6.7 * GB));
        var win = D(root, "Windows");
        Small(D(win, "System32"), 21000, (long)(7.9 * GB));
        Small(D(win, "WinSxS"), 74000, (long)(9.6 * GB));
        var inst = D(win, "Installer");
        Small(inst, 640, (long)(5.2 * GB));
        Small(D(D(win, "SoftwareDistribution"), "Download"), 420, (long)(6.1 * GB));
        Small(D(win, "Temp"), 300, 410 * MB);
        D(root, "$Recycle.Bin").Flags |= NodeFlags.Unreadable;
        var pd = D(root, "ProgramData");
        Small(D(pd, "Package Cache"), 1200, (long)(3.2 * GB));
        F(root, ("hiberfil.sys", (long)(12.7 * GB)), ("pagefile.sys", 16 * GB), ("swapfile.sys", 256 * MB));
        var link = D(root, "Documents and Settings");
        link.Flags |= NodeFlags.Link;
        Scanner.Totals(root);

        var exts = new Dictionary<string, ExtStat>();
        var top = new List<FileHit>();
        var stack = new Stack<DirNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var c in n.Dirs) stack.Push(c);
            foreach (var f in n.Files)
            {
                var e = Path.GetExtension(f.Name).ToLowerInvariant();
                if (!exts.TryGetValue(e, out var s)) exts[e] = s = new ExtStat { Ext = e };
                s.Count++;
                s.Size += f.Size;
                s.Alloc += f.Alloc;
                top.Add(new FileHit(Path.Join(n.FullPath, f.Name), f.Size, f.Alloc, f.LastWrite));
            }

            if (n.SmallCount > 0)
            {
                var e = n.Name switch { "System32" or "WinSxS" => ".dll", "Temp" => ".tmp", "Cache" => "", "Pictures" => ".jpg", "Documents" => ".docx", "Installer" => ".msp", "Download" => ".cab", _ => ".json" };
                if (!exts.TryGetValue(e, out var s)) exts[e] = s = new ExtStat { Ext = e };
                s.Count += n.SmallCount;
                s.Size += n.SmallSize;
                s.Alloc += n.SmallAlloc;
            }
        }

        return new ScanResult
        {
            Root = root, TopFiles = top.OrderByDescending(t => t.Alloc).Take(100).ToList(), Extensions = exts.Values.OrderByDescending(e => e.Alloc).ToList(),
            UnreadableCount = 0, LinksSkipped = 1, Elapsed = TimeSpan.FromSeconds(6.8), ClusterSize = 4096,
        };
    }

    private static List<WasteItem> SampleWaste()
    {
        var places = Places.Test(Path.Combine(Dir!, "sample-pc"));
        var list = WasteFinder.Items(new Places
        {
            SystemRoot = @"C:\", Windows = @"C:\Windows", ProgramData = @"C:\ProgramData", Profiles = [new UserProfile("alex", @"C:\Users\alex", true)], DriveRoots = [@"C:\", @"D:\"],
            IsTest = true, Elevated = true, IsRunning = places.IsRunning,
        });
        const long MB = 1 << 20, GB = 1L << 30;
        var sizes = new Dictionary<string, (long Size, long Files)>
        {
            ["usertemp"] = ((long)(2.31 * GB), 9100), ["wintemp"] = (412 * MB, 310), ["wu"] = ((long)(6.12 * GB), 420), ["do"] = ((long)(1.84 * GB), 96),
            ["winold"] = ((long)(24.6 * GB), 180_000), ["hiber"] = ((long)(12.7 * GB), 1), ["pagefile"] = ((long)(16.25 * GB), 2), ["dumps"] = (1100 * MB, 3),
            ["appdumps"] = (240 * MB, 6), ["wer"] = (96 * MB, 44), ["userwer"] = (12 * MB, 9), ["edge"] = (812 * MB, 3100), ["chrome"] = (1300 * MB, 4100),
            ["firefox"] = (0, 0), ["thumbs"] = (380 * MB, 9), ["patchcache"] = (1400 * MB, 2200), ["downloads"] = ((long)(9.2 * GB), 344), ["winsxs"] = ((long)(9.6 * GB), 74000),
        };
        var result = new List<WasteItem>();
        var drive = 0;
        foreach (var i in list)
        {
            if (i.Id == "recycle")
            {
                (i.Size, i.Files) = drive++ == 0 ? ((long)(3.4 * GB), 212) : (640 * MB, 31);
            }
            else if (sizes.TryGetValue(i.Id, out var s))
            {
                (i.Size, i.Files) = s;
            }

            i.Exists = i.Size > 0;
            if (i.Id == "chrome") i.Blocked = "Close Google Chrome first";
            if (i.Exists) result.Add(i);
        }

        return result;
    }
}
