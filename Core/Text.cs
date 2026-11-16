using System.Globalization;

namespace pxdSpaceAnalyzer.Core;

public static class Text
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>1536 → "1.5 KB", 0 → "0 B" (1024-based, like Explorer).</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0) return "?";
        if (bytes < 1024) return bytes.ToString(C) + " B";
        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        var i = -1;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return (v >= 100 ? v.ToString("0", C) : v >= 10 ? v.ToString("0.0", C) : v.ToString("0.00", C)) + " " + units[i];
    }

    public static string Plural(long n, string one, string? many = null) => $"{n.ToString("N0", C)} {(n == 1 ? one : many ?? one + "s")}";

    public static string Count(long n) => n.ToString("N0", C);

    public static string Date(DateTime d) => d == DateTime.MinValue || d.Year < 1980 ? "" : d.ToString("yyyy-MM-dd HH:mm", C);

    public static string Percent(double p) => p >= 0.995 ? "100%" : p < 0.001 && p > 0 ? "<0.1%" : (p * 100).ToString(p < 0.1 ? "0.0" : "0", C) + "%";

    public static string Quote(string a) => a.Contains(' ') || a.Length == 0 ? "\"" + a + "\"" : a;
}
