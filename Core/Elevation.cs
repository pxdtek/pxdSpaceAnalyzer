using System.ComponentModel;
using System.Diagnostics;

namespace pxdSpaceAnalyzer.Core;

/// <summary>The app opens as the user; reading every folder and cleaning Windows' caches needs administrator rights, so it offers to restart elevated.</summary>
public static class Elevation
{
    public static bool IsElevated { get; } = Places.CheckElevated();

    public static ProcessStartInfo RestartInfo(string[] args) => new()
    {
        FileName = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "pxdSpaceAnalyzer.exe"),
        Arguments = string.Join(" ", args.Where(a => !a.Equals("--elevated", StringComparison.OrdinalIgnoreCase)).Select(Quote).Append("--elevated")),
        UseShellExecute = true,
        Verb = "runas",
        WorkingDirectory = AppContext.BaseDirectory,
    };

    private static string Quote(string a) => a.Contains(' ') || a.Length == 0 ? "\"" + a.TrimEnd('\\').Replace("\"", "\\\"") + (a.EndsWith('\\') ? "\\\\" : "") + "\"" : a;

    /// <summary>Starts an elevated copy. Null when it started; otherwise why not ("cancelled" when the prompt was cancelled).</summary>
    public static string? RestartElevated(string[] args)
    {
        try
        {
            using var p = Process.Start(RestartInfo(args));
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "cancelled";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
