using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace pxdSpaceAnalyzer.Core;

/// <summary>A Windows user profile whose caches are looked at.</summary>
public sealed record UserProfile(string Name, string Path, bool SignedIn)
{
    public string Local => System.IO.Path.Combine(Path, "AppData", "Local");
    public string Roaming => System.IO.Path.Combine(Path, "AppData", "Roaming");
}

/// <summary>
/// Where the usual waste lives. <see cref="Real"/> is this PC; <see cref="Test"/> maps every place into a made-up tree
/// (its Windows, ProgramData, Users and drive root all inside one folder), so finding and cleaning up can be checked
/// without touching anything real. The Recycle Bin, Delivery Optimization and "is the browser open" go through
/// delegates for the same reason.
/// </summary>
public sealed class Places
{
    public required string SystemRoot { get; init; }
    public required string Windows { get; init; }
    public required string ProgramData { get; init; }
    public required List<UserProfile> Profiles { get; init; }
    /// <summary>Drives with a Recycle Bin (fixed drives).</summary>
    public required List<string> DriveRoots { get; init; }
    public bool IsTest { get; init; }
    public bool Elevated { get; init; }
    public bool IsSystemAccount { get; init; }

    public Func<string, bool> IsRunning { get; init; } = name => Process.GetProcessesByName(name).Length > 0;
    public Func<string, (long Size, long Items)?> QueryRecycleBin { get; init; } = Native.QueryRecycleBin;
    public Func<string, bool> EmptyRecycleBin { get; init; } = Native.EmptyRecycleBin;
    /// <summary>Sends one file or folder to the Recycle Bin (the test tree deletes it instead, so the real Recycle Bin stays as it is).</summary>
    public Func<string, bool> Recycle { get; init; } = p => Native.ShellDelete(p, toRecycleBin: true);
    /// <summary>Clears the Delivery Optimization cache the Windows way; null result = not available here (fall back to deleting its files).</summary>
    public Func<bool?> ClearDeliveryOptimization { get; init; } = DeliveryOptimization.Clear;

    public static bool CheckElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool CheckSystem()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value == "S-1-5-18";
        }
        catch
        {
            return false;
        }
    }

    public static Places Real()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sys = Path.GetPathRoot(win) ?? @"C:\";
        var elevated = CheckElevated();
        var system = CheckSystem();
        return new Places
        {
            SystemRoot = sys,
            Windows = win,
            ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Profiles = RealProfiles(elevated, system),
            DriveRoots = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList(),
            Elevated = elevated,
            IsSystemAccount = system,
        };
    }

    /// <summary>Everything under <paramref name="root"/>: root\Windows, root\ProgramData, root\Users\*, root as the only drive.</summary>
    public static Places Test(string root, ISet<string>? running = null)
    {
        root = Path.GetFullPath(root);
        var users = Path.Combine(root, "Users");
        var profiles = Directory.Exists(users)
            ? Directory.GetDirectories(users).Where(d => Path.GetFileName(d) is not ("Public" or "Default")).Order(StringComparer.OrdinalIgnoreCase)
                .Select((d, i) => new UserProfile(Path.GetFileName(d), d, i == 0)).ToList()
            : [];
        var bin = Path.Combine(root, "$Recycle.Bin");
        return new Places
        {
            SystemRoot = root.EndsWith('\\') ? root : root + "\\",
            Windows = Path.Combine(root, "Windows"),
            ProgramData = Path.Combine(root, "ProgramData"),
            Profiles = profiles,
            DriveRoots = [root.EndsWith('\\') ? root : root + "\\"],
            IsTest = true,
            Elevated = true,
            IsRunning = name => running?.Contains(name) == true,
            QueryRecycleBin = _ => Directory.Exists(bin) ? (Measure.Folder(bin).Alloc, Directory.GetFileSystemEntries(bin, "*", SearchOption.AllDirectories).Length) : (0, 0),
            EmptyRecycleBin = _ =>
            {
                var r = new CleanResult();
                Cleaner.DeleteContents(bin, null, r);
                return r.Skipped == 0;
            },
            Recycle = p => Native.ShellDelete(p, toRecycleBin: false),
            ClearDeliveryOptimization = () => null,
        };
    }

    /// <summary>
    /// The current user's profile; with administrator rights every real profile on the PC. Running as SYSTEM (an RMM
    /// tool), the signed-in user comes first: the console session's user, else the first active Remote Desktop session's,
    /// looked up in ProfileList.
    /// </summary>
    private static List<UserProfile> RealProfiles(bool elevated, bool system)
    {
        var list = new List<UserProfile>();
        string? signedInSid = null;
        if (system)
        {
            try
            {
                if (Native.SignedInUser() is { } who)
                    signedInSid = new NTAccount(who).Translate(typeof(SecurityIdentifier)).Value;
            }
            catch
            {
                /* nobody signed in */
            }
        }
        else
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                signedInSid = id.User?.Value;
            }
            catch
            {
                /* below */
            }
        }

        var all = new List<(string Sid, string Path)>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            foreach (var sid in key?.GetSubKeyNames() ?? [])
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                using var k = key!.OpenSubKey(sid);
                if (k?.GetValue("ProfileImagePath") is string p && Directory.Exists(Environment.ExpandEnvironmentVariables(p)))
                    all.Add((sid, Environment.ExpandEnvironmentVariables(p)));
            }
        }
        catch
        {
            /* no registry: just the current user */
        }

        if (!system)
        {
            var me = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            list.Add(new UserProfile(Path.GetFileName(me), me, true));
        }
        else if (all.FirstOrDefault(a => a.Sid == signedInSid) is { Path: not null } signed)
        {
            list.Add(new UserProfile(Path.GetFileName(signed.Path), signed.Path, true));
        }

        if (elevated || system)
            foreach (var (_, p) in all)
                if (!list.Any(u => u.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new UserProfile(Path.GetFileName(p), p, false));
        return list;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    public static bool Inside(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\');
        var r = Path.GetFullPath(root).TrimEnd('\\');
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The Delivery Optimization cache, cleared with its own cmdlet (Windows 10 and later, administrator).</summary>
public static class DeliveryOptimization
{
    /// <summary>True cleared, false failed, null when the cmdlet isn't there.</summary>
    public static bool? Clear()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"if (Get-Command Delete-DeliveryOptimizationCache -ErrorAction SilentlyContinue) { Delete-DeliveryOptimizationCache -Force -ErrorAction Stop; exit 0 } else { exit 9 }\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(120_000)) return false;
            return p.ExitCode switch { 0 => true, 9 => null, _ => false };
        }
        catch
        {
            return null;
        }
    }
}
