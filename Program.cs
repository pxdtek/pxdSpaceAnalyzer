using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using pxdSpaceAnalyzer.Core;

namespace pxdSpaceAnalyzer;

internal static class Program
{
    public const string AppId = "pxdSpaceAnalyzer";

    /// <summary>Where updates come from: this app's releases (asked at pxdtek.com first, GitHub as the fallback) and its Windows zip (the same asset the pxd Toolkit installs).</summary>
    public const string UpdateRepo = "pxdtek/pxdSpaceAnalyzer";
    public const string UpdateAsset = @"^pxdSpaceAnalyzer-.*-win-x64\.zip$";

    /// <summary>Runs that never update first: the build step, the elevated copy the app starts itself (the first copy already updated).</summary>
    public static readonly string[] NoUpdateArgs = ["--write-icon", "--elevated", "--report"];

    /// <summary>Why the app runs without administrator rights, for the notice in the window (null when elevated).</summary>
    public static string? LimitedReason { get; set; }

    /// <summary>A folder or drive to scan as soon as the window opens (--scan PATH; the elevated restart passes it on).</summary>
    public static string? ScanOnStart { get; private set; }

    public static string DataFolder =>
        Environment.GetEnvironmentVariable("PXDSA_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId);

    private static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? After(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // pxd standard rule 6: an installed copy updates itself before it opens. A no-op for dev builds (bin\),
        // --self-test, PXD_NO_UPDATE=1, offline or when neither pxdtek.com nor GitHub answers in time.
        if (!args.Any(a => NoUpdateArgs.Contains(a, StringComparer.OrdinalIgnoreCase))
            && pxdStandard.pxdUpdate.BeforeStart(UpdateRepo, UpdateAsset, args))
            return 0;

        if (After(args, "--report") is { } reportPath)
            return Report.Run(reportPath, Has(args, "--waste"));

        if (After(args, "--write-icon") is { } icon)
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            BrandIcon.Write(icon);
            return 0;
        }

        if (After(args, "--self-test") is { } testDir)
        {
            SelfTest.Dir = Path.GetFullPath(testDir);
            SelfTest.Sample = Has(args, "--sample");
            Directory.CreateDirectory(SelfTest.Dir);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PXDSA_DATA")))
                Environment.SetEnvironmentVariable("PXDSA_DATA", Path.Combine(SelfTest.Dir, "data"));
            Environment.SetEnvironmentVariable("PXD_SHARED_DIR", Path.Combine(SelfTest.Dir, "shared"));
            pxdStandard.pxdChrome.Configure(AppId, DataFolder);
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            using var done = new CancellationTokenSource();
            Dispatcher.UIThread.Post(async () =>
            {
                await SelfTest.RunAsync();
                done.Cancel();
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            return SelfTest.ExitCode;
        }

        ScanOnStart = After(args, "--scan");
        if (!Elevation.IsElevated)
            LimitedReason = "Running without administrator rights: some folders can't be read, and Windows' own caches can't be cleaned.";

        if (!SingleInstance.Claim(SingleInstance.Name(AppId), args))
            return 0;

        pxdStandard.pxdChrome.Configure(AppId, DataFolder);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
            .LogToTrace();
}
