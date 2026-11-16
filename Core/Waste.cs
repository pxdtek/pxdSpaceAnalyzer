using System.Diagnostics;
using System.Text.RegularExpressions;

namespace pxdSpaceAnalyzer.Core;

public enum CleanHow
{
    /// <summary>Only reported (Windows.old, hiberfil.sys, the Installer cache, Downloads, WinSxS...).</summary>
    None,
    /// <summary>Deleted for good: caches and temp files apps make again.</summary>
    Permanent,
    /// <summary>Sent to the Recycle Bin (crash dumps: someone may still want one).</summary>
    RecycleBin,
    EmptyRecycleBin,
    DeliveryOptimization,
}

public enum TargetKind { Contents, Pattern, File }

/// <summary>Where an item's files are: a folder's contents, files matching a wildcard in a folder, or one file.</summary>
public sealed record Target(string Path, TargetKind Kind = TargetKind.Contents, string? Pattern = null);

public sealed class WasteItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Note { get; init; }
    public string? User { get; init; }
    public List<Target> Targets { get; init; } = [];
    public CleanHow How { get; init; }
    public bool NeedsAdmin { get; init; }
    /// <summary>Only files older than this many days are counted and cleaned (temp folders).</summary>
    public int OlderThanDays { get; init; }
    /// <summary>The browser process that must be closed first.</summary>
    public string? Process { get; init; }
    public string? ProcessName { get; init; }

    public long Size { get; set; }
    public long Files { get; set; }
    /// <summary>Part of it (or all of it) couldn't be read without administrator rights.</summary>
    public bool Unreadable { get; set; }
    /// <summary>Why it can't be cleaned now (the browser is open, needs administrator rights...).</summary>
    public string? Blocked { get; set; }
    public bool Exists { get; set; }

    public bool CanClean => How != CleanHow.None && Blocked is null && Size > 0;
    public string Display => User is null ? Name : $"{Name} ({User})";

    public string HowText => How switch
    {
        CleanHow.Permanent => "deleted permanently (not to the Recycle Bin)",
        CleanHow.RecycleBin => "moved to the Recycle Bin",
        CleanHow.EmptyRecycleBin => "removed from the Recycle Bin for good",
        CleanHow.DeliveryOptimization => "cleared by Windows (Delivery Optimization), for good",
        _ => "only reported",
    };

    public string FirstPath => Targets.Count == 0 ? "" : Targets[0].Path;
}

/// <summary>
/// The usual waste: temp files, update and Delivery Optimization caches, Windows.old, the Recycle Bins, hiberfil.sys,
/// the page file, crash dumps, error reports, browser caches, the thumbnail cache, the Installer patch cache, Downloads
/// and the component store. Each comes with its size and a plain-English note. The same list (same ids) is in the
/// PowerShell edition.
/// </summary>
public static class WasteFinder
{
    public static List<WasteItem> Items(Places p)
    {
        var list = new List<WasteItem>();
        var many = p.Profiles.Count > 1;
        string? Who(UserProfile u) => many ? u.Name : null;

        foreach (var u in p.Profiles)
            list.Add(new WasteItem
            {
                Id = "usertemp", Name = "Your temp files", User = Who(u), Targets = [new(Path.Combine(u.Local, "Temp"))], How = CleanHow.Permanent, OlderThanDays = 1,
                Note = "Left behind by installers and apps. Files older than a day are safe to delete; newer ones may still be in use, so they stay.",
            });
        list.Add(new WasteItem
        {
            Id = "wintemp", Name = "Windows temp files", Targets = [new(Path.Combine(p.Windows, "Temp"))], How = CleanHow.Permanent, OlderThanDays = 1, NeedsAdmin = true,
            Note = "Temp files from Windows, services and installers. Files older than a day are safe to delete.",
        });
        list.Add(new WasteItem
        {
            Id = "wu", Name = "Windows Update downloads", Targets = [new(Path.Combine(p.Windows, "SoftwareDistribution", "Download"))], How = CleanHow.Permanent, NeedsAdmin = true,
            Note = "Update files already installed (or downloaded again if needed). Safe to delete; files in use by an update running now are skipped. Nothing is stopped.",
        });
        list.Add(new WasteItem
        {
            Id = "do", Name = "Delivery Optimization cache",
            Targets = [new(Path.Combine(p.Windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")),
                       new(Path.Combine(p.Windows, "SoftwareDistribution", "DeliveryOptimization"))],
            How = CleanHow.DeliveryOptimization, NeedsAdmin = true,
            Note = "Copies of updates Windows shares with other PCs. Safe to clear; Windows downloads again what it needs.",
        });
        list.Add(new WasteItem
        {
            Id = "winold", Name = "Windows.old (previous Windows)", Targets = [new(Path.Combine(p.SystemRoot, "Windows.old"))],
            Note = "Your previous Windows, kept for going back after an upgrade (about 10 days). Remove it in Settings > System > Storage > Temporary files > Previous Windows installation(s).",
        });
        foreach (var d in p.DriveRoots)
            list.Add(new WasteItem
            {
                Id = "recycle", Name = $"Recycle Bin ({d.TrimEnd('\\')})", Targets = [new(d)], How = CleanHow.EmptyRecycleBin,
                Note = "Files you deleted. Emptying it removes them for good.",
            });
        list.Add(new WasteItem
        {
            Id = "hiber", Name = "Hibernation file (hiberfil.sys)", Targets = [new(Path.Combine(p.SystemRoot, "hiberfil.sys"), TargetKind.File)],
            Note = "Holds memory while the PC hibernates; also used by Fast startup. If you never hibernate, \"powercfg -h off\" in an administrator prompt removes it (Fast startup goes too). Not done here.",
        });
        list.Add(new WasteItem
        {
            Id = "pagefile", Name = "Page file (pagefile.sys, swapfile.sys)",
            Targets = [new(Path.Combine(p.SystemRoot, "pagefile.sys"), TargetKind.File), new(Path.Combine(p.SystemRoot, "swapfile.sys"), TargetKind.File)],
            Note = "Virtual memory, managed by Windows. Leave it; change its size only in System > Advanced system settings > Performance.",
        });
        list.Add(new WasteItem
        {
            Id = "dumps", Name = "Crash dumps (Windows)",
            Targets = [new(Path.Combine(p.Windows, "MEMORY.DMP"), TargetKind.File), new(Path.Combine(p.Windows, "Minidump")), new(Path.Combine(p.Windows, "LiveKernelReports"))],
            How = CleanHow.RecycleBin, NeedsAdmin = true,
            Note = "Memory snapshots from blue screens and driver hangs. Only useful for finding the cause; safe to remove (they go to the Recycle Bin).",
        });
        foreach (var u in p.Profiles)
            list.Add(new WasteItem
            {
                Id = "appdumps", Name = "App crash dumps", User = Who(u), Targets = [new(Path.Combine(u.Local, "CrashDumps"))], How = CleanHow.RecycleBin,
                Note = "Snapshots of apps that crashed. Safe to remove (they go to the Recycle Bin).",
            });
        list.Add(new WasteItem
        {
            Id = "wer", Name = "Error reports (Windows)",
            Targets = [new(Path.Combine(p.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue")), new(Path.Combine(p.ProgramData, "Microsoft", "Windows", "WER", "ReportArchive")),
                       new(Path.Combine(p.ProgramData, "Microsoft", "Windows", "WER", "Temp"))],
            How = CleanHow.Permanent, NeedsAdmin = true,
            Note = "Problem reports waiting to be sent to Microsoft, or already sent. Safe to delete.",
        });
        foreach (var u in p.Profiles)
            list.Add(new WasteItem
            {
                Id = "userwer", Name = "Error reports", User = Who(u),
                Targets = [new(Path.Combine(u.Local, "Microsoft", "Windows", "WER", "ReportQueue")), new(Path.Combine(u.Local, "Microsoft", "Windows", "WER", "ReportArchive"))],
                How = CleanHow.Permanent,
                Note = "Your apps' problem reports, waiting or already sent. Safe to delete.",
            });
        foreach (var u in p.Profiles)
        {
            list.Add(Browser("edge", "Microsoft Edge cache", "msedge", "Microsoft Edge", Path.Combine(u.Local, "Microsoft", "Edge", "User Data"), Who(u)));
            list.Add(Browser("chrome", "Google Chrome cache", "chrome", "Google Chrome", Path.Combine(u.Local, "Google", "Chrome", "User Data"), Who(u)));
            list.Add(new WasteItem
            {
                Id = "firefox", Name = "Firefox cache", User = Who(u), How = CleanHow.Permanent, Process = "firefox", ProcessName = "Firefox",
                Targets = Subfolders(Path.Combine(u.Local, "Mozilla", "Firefox", "Profiles"), "cache2"),
                Note = "Web pages and pictures Firefox keeps to load sites faster. Safe to delete while Firefox is closed; sign-ins and history stay.",
            });
        }

        foreach (var u in p.Profiles)
            list.Add(new WasteItem
            {
                Id = "thumbs", Name = "Thumbnail cache", User = Who(u), How = CleanHow.Permanent,
                Targets = [new(Path.Combine(u.Local, "Microsoft", "Windows", "Explorer"), TargetKind.Pattern, "thumbcache_*.db")],
                Note = "Picture previews Explorer keeps. Safe to delete; Explorer makes them again (files it has open are skipped).",
            });
        list.Add(new WasteItem
        {
            Id = "patchcache", Name = "Installer patch cache ($PatchCache$)", Targets = [new(Path.Combine(p.Windows, "Installer", "$PatchCache$"))],
            Note = "Backup copies of files patched by Windows Installer. Never delete anything in C:\\Windows\\Installer by hand: programs need it to update and uninstall. Reported only.",
        });
        foreach (var u in p.Profiles)
            list.Add(new WasteItem
            {
                Id = "downloads", Name = "Downloads folder", User = Who(u), Targets = [new(Path.Combine(u.Path, "Downloads"))],
                Note = "Your own downloads: old installers and zips pile up here. Look through it yourself; nothing is deleted automatically.",
            });
        list.Add(new WasteItem
        {
            Id = "winsxs", Name = "Component store (WinSxS)", Targets = [new(Path.Combine(p.Windows, "WinSxS"))],
            Note = "Windows' own components. Most of it is shared with Windows itself (hard links), so Explorer's size is misleading. Never delete it; \"Analyze\" (as administrator) asks DISM how much could be cleaned.",
        });
        return list;
    }

    private static WasteItem Browser(string id, string name, string process, string app, string userData, string? user) => new()
    {
        Id = id, Name = name, User = user, How = CleanHow.Permanent, Process = process, ProcessName = app,
        Targets = BrowserCaches(userData),
        Note = $"Web pages, scripts and pictures {app} keeps to load sites faster. Safe to delete while {app} is closed; sign-ins, passwords and history stay.",
    };

    /// <summary>Cache, Code Cache and GPUCache of each Chromium profile (Default, Profile 1...).</summary>
    private static List<Target> BrowserCaches(string userData)
    {
        var list = new List<Target>();
        try
        {
            if (!Directory.Exists(userData)) return [new(Path.Combine(userData, "Default", "Cache"))];
            foreach (var prof in Directory.GetDirectories(userData))
            {
                var n = Path.GetFileName(prof);
                if (n != "Default" && !n.StartsWith("Profile ", StringComparison.Ordinal) && n != "Guest Profile") continue;
                foreach (var c in new[] { "Cache", "Code Cache", "GPUCache" })
                    if (Directory.Exists(Path.Combine(prof, c))) list.Add(new(Path.Combine(prof, c)));
            }
        }
        catch
        {
            /* not readable */
        }

        return list;
    }

    private static List<Target> Subfolders(string parent, string child)
    {
        try
        {
            if (Directory.Exists(parent))
                return Directory.GetDirectories(parent).Select(d => Path.Combine(d, child)).Where(Directory.Exists).Select(d => new Target(d)).ToList();
        }
        catch
        {
            /* not readable */
        }

        return [];
    }

    /// <summary>Measures one item and works out whether it can be cleaned now.</summary>
    public static void Measure(WasteItem item, Places p)
    {
        long size = 0, files = 0;
        var unreadable = false;
        var exists = false;
        if (item.How == CleanHow.EmptyRecycleBin)
        {
            foreach (var t in item.Targets)
            {
                if (p.QueryRecycleBin(t.Path) is { } q)
                {
                    size += q.Size;
                    files += q.Items;
                    exists = true;
                }
                else unreadable = true;
            }
        }
        else
        {
            foreach (var t in item.Targets)
            {
                var s = t.Kind switch
                {
                    TargetKind.File => Core.Measure.File(t.Path),
                    TargetKind.Pattern => Core.Measure.Pattern(t.Path, t.Pattern!),
                    _ => Core.Measure.Folder(t.Path, item.OlderThanDays > 0 ? DateTime.Now.AddDays(-item.OlderThanDays) : null),
                };
                size += s.Alloc;
                files += s.Files;
                unreadable |= s.Unreadable;
                exists |= s.Exists;
            }
        }

        item.Size = size;
        item.Files = files;
        item.Unreadable = unreadable;
        item.Exists = exists;
        item.Blocked = null;
        if (item.How != CleanHow.None)
        {
            if (item.NeedsAdmin && !p.Elevated) item.Blocked = "Needs administrator rights";
            else if (item.Process is { } proc && p.IsRunning(proc)) item.Blocked = $"Close {item.ProcessName} first";
        }
    }

    /// <summary>Finds and measures everything (several at a time). Items that aren't there at all are left out.</summary>
    public static List<WasteItem> Find(Places p, CancellationToken cancel = default)
    {
        var items = Items(p);
        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel }, i => Measure(i, p));
        return items.Where(i => i.Exists || i.Unreadable || i.Id == "recycle").ToList();
    }

    // ---------- Component store (DISM) ----------

    public sealed record StoreReport(long ActualSize, long SharedWithWindows, long Backups, long CacheAndTemp, int Reclaimable, bool CleanupRecommended);

    /// <summary>Runs DISM /AnalyzeComponentStore (administrator only; takes a minute or two). Null when it couldn't.</summary>
    public static async Task<StoreReport?> AnalyzeComponentStoreAsync(CancellationToken cancel = default)
    {
        if (!Places.CheckElevated()) return null;
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "dism.exe"), "/English /Online /Cleanup-Image /AnalyzeComponentStore")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync(cancel);
            await proc.WaitForExitAsync(cancel);
            return ParseDism(output);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads DISM's report ("Actual Size of Component Store : 8.12 GB" and so on).</summary>
    public static StoreReport? ParseDism(string text)
    {
        long Read(string label)
        {
            var m = Regex.Match(text, Regex.Escape(label) + @"\s*:\s*([\d.,]+)\s*(bytes|KB|MB|GB|TB)", RegexOptions.IgnoreCase);
            if (!m.Success) return -1;
            var v = double.Parse(m.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
            var mult = m.Groups[2].Value.ToUpperInvariant() switch { "KB" => 1L << 10, "MB" => 1L << 20, "GB" => 1L << 30, "TB" => 1L << 40, _ => 1L };
            return (long)(v * mult);
        }

        var actual = Read("Actual Size of Component Store");
        if (actual < 0) return null;
        var rec = Regex.Match(text, @"Component Store Cleanup Recommended\s*:\s*(Yes|No)", RegexOptions.IgnoreCase);
        var pkgs = Regex.Match(text, @"Number of Reclaimable Packages\s*:\s*(\d+)", RegexOptions.IgnoreCase);
        return new StoreReport(actual, Read("Shared with Windows"), Read("Backups and Disabled Features"), Read("Cache and Temporary Data"),
            pkgs.Success ? int.Parse(pkgs.Groups[1].Value) : 0, rec.Success && rec.Groups[1].Value.Equals("Yes", StringComparison.OrdinalIgnoreCase));
    }
}
