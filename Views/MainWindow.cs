using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using pxdSpaceAnalyzer.Core;
using Path = System.IO.Path;

namespace pxdSpaceAnalyzer.Views;

public enum AppPage { Home, Folders, Treemap, Files, Types, Waste }

/// <summary>
/// The whole app: a sidebar of drives (used/free bars) and views, a home page to pick a drive or folder, and the scan's
/// views: the folder tree (MainWindow.Tree.cs), the treemap (MainWindow.Map.cs), the biggest files and file types
/// (MainWindow.Lists.cs) and the usual waste with clean-up (MainWindow.Waste.cs). Built in code, pxd neon theme.
/// </summary>
public sealed partial class MainWindow : Window
{
    internal static IBrush Brush(string c) => new SolidColorBrush(Color.Parse(c));
    internal static readonly IBrush Muted = Brush("#8A92A6");
    internal static readonly IBrush Good = Brush("#19F58C");
    internal static readonly IBrush Warn = Brush("#F5B14C");
    internal static readonly IBrush Bad = Brush("#F2666A");
    internal static readonly IBrush Info = Brush("#00C8FF");
    internal static readonly IBrush TextB = Brush("#E8EAF0");
    internal static readonly IBrush BarBg = Brush("#1A1F2A");
    internal static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    internal static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    /// <summary>Asks before anything is deleted (a pxd dialog). The self-test answers it itself.</summary>
    public static Func<Window, Confirm, Task<bool>> Ask { get; set; } = (owner, c) => ConfirmDialog.AskAsync(owner, c);

    /// <summary>Sends a file or folder from the tree to the Recycle Bin. The self-test deletes inside its own folder instead.</summary>
    public static Func<string, bool> RecycleAction { get; set; } = p => Native.ShellDelete(p, toRecycleBin: true);

    /// <summary>Drives shown in the sidebar and on the home page (the self-test and screenshots use made-up ones).</summary>
    public static Func<List<DriveSpace>> DriveSource { get; set; } = DriveSpace.Fixed;

    private readonly Settings _settings;
    private readonly Places _places;
    private readonly Dictionary<AppPage, Button> _nav = [];
    private readonly Border _notice = new() { Classes = { "notice" }, IsVisible = false, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _noticeText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _noticeAction = new() { Classes = { "primary" }, MinHeight = 28, Padding = new Thickness(12, 2), Margin = new Thickness(8, 0, 0, 0), IsVisible = false };
    private Action? _noticeDo;
    private readonly TextBlock _status = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { Width = 180, IsIndeterminate = true, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancel = new() { Content = "Cancel", IsVisible = false, MinHeight = 28, Padding = new Thickness(12, 2), Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly StackPanel _drivesNav = new() { Spacing = 4, Margin = new Thickness(10, 0, 10, 14) };
    private readonly Grid _stage = new();
    private readonly Dictionary<AppPage, Control> _pages = [];
    private readonly TextBox _pathBox = new() { PlaceholderText = @"A folder to scan, like D:\Games", VerticalAlignment = VerticalAlignment.Center };
    private CancellationTokenSource? _scanCancel;
    private ScanResult? _result;
    private string? _scanPath;
    private bool _busy;
    private AppPage _page = AppPage.Home;

    public bool SelfTestMode { get; set; }
    public ScanResult? Result => _result;
    public AppPage CurrentPage => _page;
    public bool Busy => _busy;
    public bool NoticeShowing => _notice.IsVisible;
    public string NoticeText => _noticeText.Text ?? "";
    public string StatusText => _status.Text ?? "";

    public MainWindow() : this(null)
    {
    }

    /// <param name="places">Where the usual waste is looked for; null = this PC (the self-test passes a made-up tree).</param>
    public MainWindow(Places? places)
    {
        _settings = Settings.Load();
        _places = places ?? Places.Real();
        Title = "pxd Space Analyzer";
        Icon = BrandIcon.Window();
        Width = 1280;
        Height = 820;
        MinWidth = 1000;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*") };
        body.Children.Add(BuildSidebar());
        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(22, 16, 22, 10) };
        Grid.SetColumn(main, 1);
        body.Children.Add(main);

        var ng = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        ng.Children.Add(_noticeText);
        _noticeAction.Click += (_, _) =>
        {
            var act = _noticeDo;
            _notice.IsVisible = false;
            act?.Invoke();
        };
        Grid.SetColumn(_noticeAction, 1);
        ng.Children.Add(_noticeAction);
        var dismiss = new Button { Content = "Dismiss", Classes = { "ghost" }, MinHeight = 28, Padding = new Thickness(10, 2), Margin = new Thickness(8, 0, 0, 0) };
        dismiss.Click += (_, _) => _notice.IsVisible = false;
        Grid.SetColumn(dismiss, 2);
        ng.Children.Add(dismiss);
        _notice.Child = ng;
        main.Children.Add(_notice);

        var head = BuildHeader();
        Grid.SetRow(head, 1);
        main.Children.Add(head);

        Grid.SetRow(_stage, 2);
        main.Children.Add(_stage);
        _pages[AppPage.Home] = BuildHome();
        _pages[AppPage.Folders] = BuildTreePage();
        _pages[AppPage.Treemap] = BuildMapPage();
        _pages[AppPage.Files] = BuildFilesPage();
        _pages[AppPage.Types] = BuildTypesPage();
        _pages[AppPage.Waste] = BuildWastePage();
        foreach (var p in _pages.Values)
        {
            p.IsVisible = false;
            _stage.Children.Add(p);
        }

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(_status);
        Grid.SetColumn(_progress, 1);
        bar.Children.Add(_progress);
        _cancel.Click += (_, _) => _scanCancel?.Cancel();
        Grid.SetColumn(_cancel, 2);
        bar.Children.Add(_cancel);
        Grid.SetRow(bar, 3);
        main.Children.Add(bar);

        Content = pxdStandard.pxdChrome.Wrap(this, body, Title, BrandIcon.Render(36));
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F5 && !_busy && _scanPath is not null) _ = ScanAsync(_scanPath);
            else if (e.Key == Key.Escape && _busy) _scanCancel?.Cancel();
        };

        if (Program.LimitedReason is { } why && !_places.IsTest)
            ShowNotice(why, action: "Restart as administrator", act: RestartElevated);

        RefreshDrives();
        ShowPage(AppPage.Home);
        _status.Text = "Pick a drive or a folder to see what's filling it.";
        if (Program.ScanOnStart is { } start && !_places.IsTest)
            Opened += async (_, _) => await ScanAsync(start);
    }

    // ---------- Shell ----------

    private Control BuildSidebar()
    {
        var dock = new DockPanel { Background = Brush("#0B0E13") };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(16, 16, 16, 18) };
        head.Children.Add(new Image { Source = BrandIcon.Render(72), Width = 36, Height = 36 });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "pxd", FontSize = 13, FontWeight = FontWeight.Bold, LetterSpacing = 1.4, Classes = { "neon" } });
        names.Children.Add(new TextBlock { Text = "SPACE ANALYZER", FontSize = 13, FontWeight = FontWeight.Bold, LetterSpacing = 1.4, Classes = { "neon" } });
        head.Children.Add(names);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        var foot = new StackPanel { Spacing = 8, Margin = new Thickness(12, 8, 12, 14) };
        if (!Elevation.IsElevated && !_places.IsTest)
        {
            var admin = new Button { Content = "Restart as administrator", HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(admin, "Read every folder (including Windows' own and other users') and clean Windows' caches. Windows asks first.");
            admin.Click += (_, _) => RestartElevated();
            foot.Children.Add(admin);
        }
        else
        {
            foot.Children.Add(new Border { Classes = { "pill", "good" }, HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = "Administrator: reads everything" } });
        }

        foot.Children.Add(new TextBlock { Text = $"Version {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}", Classes = { "muted" }, FontSize = 11, Margin = new Thickness(4, 2, 0, 0) });
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);

        var nav = new StackPanel { Spacing = 2 };
        nav.Children.Add(new TextBlock { Text = "DRIVES", Classes = { "label" }, Margin = new Thickness(22, 0, 0, 6) });
        nav.Children.Add(_drivesNav);
        var views = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0) };
        views.Children.Add(new TextBlock { Text = "VIEW", Classes = { "label" }, Margin = new Thickness(12, 0, 0, 6) });
        (AppPage, string, string)[] items =
        [
            (AppPage.Home, "\uE80F", "Start"),
            (AppPage.Folders, "\uE8B7", "Folders"),
            (AppPage.Treemap, "\uE80A", "Treemap"),
            (AppPage.Files, "\uE8A5", "Biggest files"),
            (AppPage.Types, "\uE8FD", "File types"),
            (AppPage.Waste, "\uE74D", "The usual waste"),
        ];
        foreach (var (page, glyph, name) in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = Glyphs, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var b = new Button
            {
                Content = row, Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 8), MinHeight = 40,
            };
            b.Click += (_, _) => ShowPage(page);
            _nav[page] = b;
            views.Children.Add(b);
        }

        nav.Children.Add(views);
        dock.Children.Add(new ScrollViewer { Content = nav });
        return dock;
    }

    private readonly Button _rescan = new() { Content = "Scan again", IsVisible = false };

    private Control BuildHeader()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        var left = new StackPanel();
        left.Children.Add(_title);
        left.Children.Add(_subtitle);
        g.Children.Add(left);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var rescan = _rescan;
        ToolTip.SetTip(rescan, "Scan the same drive or folder again (F5).");
        rescan.Click += async (_, _) =>
        {
            if (!_busy && _scanPath is not null) await ScanAsync(_scanPath);
        };
        buttons.Children.Add(rescan);
        Grid.SetColumn(buttons, 1);
        g.Children.Add(buttons);
        return g;
    }

    internal static Border Pill(string text, string kind) => new() { Classes = { "pill", kind }, Child = new TextBlock { Text = text } };

    /// <summary>A thin bar filled to <paramref name="fraction"/>.</summary>
    internal static Control Bar(double fraction, IBrush fill, double height = 6, double width = double.NaN)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        var g = new Grid { Height = height, Width = width, VerticalAlignment = VerticalAlignment.Center, ColumnDefinitions = new ColumnDefinitions($"{fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}*,{(1 - fraction).ToString(System.Globalization.CultureInfo.InvariantCulture)}*") };
        g.Children.Add(new Border { Background = BarBg, CornerRadius = new CornerRadius(height / 2), [Grid.ColumnSpanProperty] = 2 });
        if (fraction > 0) g.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(height / 2), MinWidth = height });
        return g;
    }

    public void ShowNotice(string text, bool good = false, string? action = null, Action? act = null)
    {
        _notice.Background = Brush(good ? "#0C2A1D" : "#2A2210");
        _notice.BorderBrush = Brush(good ? "#146B44" : "#7A5A1A");
        _noticeText.Foreground = Brush(good ? "#7DF5BC" : "#F5C97A");
        _noticeText.Text = text;
        _noticeAction.Content = action;
        _noticeAction.IsVisible = action is not null;
        _noticeDo = act;
        _notice.IsVisible = true;
    }

    private void RestartElevated()
    {
        if (SelfTest.Active) return;
        var why = Elevation.RestartElevated(_scanPath is null ? [] : ["--scan", _scanPath]);
        if (why is null)
        {
            SingleInstance.Release();
            Close();
        }
        else
        {
            ShowNotice(why == "cancelled" ? "Still running without administrator rights: the Windows prompt was cancelled." : $"Couldn't get administrator rights ({why}).");
        }
    }

    public void ShowPage(AppPage page)
    {
        if (page is not (AppPage.Home or AppPage.Waste) && _result is null) return;   // nothing scanned yet
        _page = page;
        foreach (var (p, c) in _pages) c.IsVisible = p == page;
        foreach (var (p, b) in _nav)
        {
            if (p == page) b.Foreground = Good;
            else b.ClearValue(ForegroundProperty);
            b.Opacity = p is AppPage.Home or AppPage.Waste || _result is not null ? 1 : 0.4;
        }

        _rescan.IsVisible = _scanPath is not null && page is not (AppPage.Home or AppPage.Waste);

        switch (page)
        {
            case AppPage.Home:
                _title.Text = "What's filling your drives?";
                _subtitle.Text = "Pick a drive or a folder. The scan is read-only: nothing is changed until you choose to delete something.";
                break;
            case AppPage.Waste:
                _title.Text = "The usual waste";
                _subtitle.Text = "Temp files, caches, old updates and other things Windows and apps leave behind, with what each one is.";
                if (!_wasteLoaded && !_wasteBusy) _ = FindWasteAsync();
                break;
            default:
                SetScanTitle();
                break;
        }

        if (page == AppPage.Treemap) RefreshMap();
    }

    private void SetScanTitle()
    {
        if (_result is not { } r) return;
        _title.Text = r.Root.FullPath;
        var parts = new List<string>
        {
            $"{Text.Size(r.Root.Alloc)} on disk",
            Text.Plural(r.Root.FileCount, "file"),
            Text.Plural(r.Root.DirCount, "folder"),
            $"scanned in {r.Elapsed.TotalSeconds:0.0} s",
        };
        if (DriveSource().FirstOrDefault(d => d.Root.Equals(r.Root.FullPath, StringComparison.OrdinalIgnoreCase)) is { } drive)
            parts.Insert(1, $"{Text.Size(drive.Free)} free of {Text.Size(drive.Total)}");
        if (r.Cancelled) parts.Add("stopped early");
        _subtitle.Text = string.Join("  ·  ", parts);
    }

    // ---------- Home and drives ----------

    private readonly WrapPanel _driveCards = new() { Orientation = Orientation.Horizontal };

    private Control BuildHome()
    {
        var s = new StackPanel { Spacing = 14 };
        s.Children.Add(_driveCards);
        var folder = new Border { Classes = { "card" }, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        var fs = new StackPanel { Spacing = 10 };
        fs.Children.Add(new TextBlock { Text = "A folder", FontSize = 16, FontWeight = FontWeight.Bold });
        fs.Children.Add(new TextBlock { Text = "Type or paste a path, or browse to it.", Classes = { "muted" } });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        row.Children.Add(_pathBox);
        _pathBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && (_pathBox.Text ?? "").Trim().Length > 0) await ScanAsync(_pathBox.Text!.Trim());
        };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += async (_, _) => await BrowseAsync();
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        var go = new Button { Content = "Scan", Classes = { "primary" }, Margin = new Thickness(8, 0, 0, 0) };
        go.Click += async (_, _) =>
        {
            if ((_pathBox.Text ?? "").Trim().Length > 0) await ScanAsync(_pathBox.Text!.Trim());
        };
        Grid.SetColumn(go, 2);
        row.Children.Add(go);
        fs.Children.Add(row);
        folder.Child = fs;
        s.Children.Add(folder);
        return new ScrollViewer { Content = s };
    }

    public void RefreshDrives()
    {
        var drives = DriveSource();
        _drivesNav.Children.Clear();
        _driveCards.Children.Clear();
        foreach (var d in drives)
        {
            var used = d.Total > 0 ? (double)(d.Total - d.Free) / d.Total : 0;
            var fill = used > 0.9 ? Bad : used > 0.75 ? Warn : Good;

            // Sidebar: letter, label and a thin bar.
            var ns = new StackPanel { Spacing = 4 };
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            top.Children.Add(new TextBlock { Text = d.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            top.Children.Add(new TextBlock { Text = Text.Size(d.Free) + " free", Classes = { "muted" }, FontSize = 11, [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center });
            ns.Children.Add(top);
            ns.Children.Add(Bar(used, fill, 5));
            var nb = new Button { Content = ns, Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 7) };
            ToolTip.SetTip(nb, $"Scan {d.Root}");
            nb.Click += async (_, _) => await ScanAsync(d.Root);
            _drivesNav.Children.Add(nb);

            // Home: a card per drive.
            var card = new Border { Classes = { "card" }, Width = 360, Margin = new Thickness(0, 0, 14, 14) };
            var cs = new StackPanel { Spacing = 8 };
            var ch = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            ch.Children.Add(new TextBlock { Text = "\uEDA2", FontFamily = Glyphs, FontSize = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Foreground = Info });
            var cn = new StackPanel { [Grid.ColumnProperty] = 1 };
            cn.Children.Add(new TextBlock { Text = d.Title, FontSize = 16, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
            cn.Children.Add(new TextBlock { Text = $"{d.Format}  ·  {Text.Size(d.Total)}", Classes = { "muted" }, FontSize = 12 });
            ch.Children.Add(cn);
            var scan = new Button { Content = "Scan", Classes = { "primary" }, [Grid.ColumnProperty] = 2, VerticalAlignment = VerticalAlignment.Center };
            scan.Click += async (_, _) => await ScanAsync(d.Root);
            ch.Children.Add(scan);
            cs.Children.Add(ch);
            cs.Children.Add(Bar(used, fill, 10));
            cs.Children.Add(new TextBlock { Text = $"{Text.Size(d.Total - d.Free)} used  ·  {Text.Size(d.Free)} free  ·  {Text.Percent(used)} full", Classes = { "muted" }, FontSize = 12 });
            card.Child = cs;
            _driveCards.Children.Add(card);
        }
    }

    private async Task BrowseAsync()
    {
        if (SelfTest.Active) return;
        try
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Pick a folder to scan", AllowMultiple = false });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            {
                _pathBox.Text = path;
                await ScanAsync(path);
            }
        }
        catch
        {
            /* no picker */
        }
    }

    // ---------- Scanning ----------

    public async Task ScanAsync(string path)
    {
        if (_busy)
        {
            _scanCancel?.Cancel();
            return;
        }

        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch
        {
            ShowNotice($"That isn't a folder path: {path}");
            return;
        }

        if (!Directory.Exists(path))
        {
            ShowNotice($"There's no folder at {path}.");
            return;
        }

        _scanPath = path;
        _busy = true;
        _notice.IsVisible = Program.LimitedReason is not null && _notice.IsVisible && _noticeText.Text == Program.LimitedReason;
        _progress.IsVisible = true;
        _cancel.IsVisible = true;
        _scanCancel = new CancellationTokenSource();
        var progress = new ScanProgress();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => _status.Text = $"Scanning…  {Text.Plural(progress.Files, "file")}  ·  {Text.Size(progress.Bytes)}  ·  {progress.Current}";
        timer.Start();
        _status.Text = $"Scanning {path}…";
        var cancel = _scanCancel.Token;
        ScanResult result;
        try
        {
            result = await Task.Run(() => Scanner.Scan(path, progress, cancel), CancellationToken.None);
        }
        finally
        {
            timer.Stop();
            _busy = false;
            _progress.IsVisible = false;
            _cancel.IsVisible = false;
        }

        SetResult(result);
    }

    /// <summary>Shows a finished scan (the self-test and screenshots pass one in).</summary>
    public void SetResult(ScanResult result)
    {
        _result = result;
        _scanPath = result.Root.FullPath;
        _expanded.Clear();
        _expanded.Add(result.Root);
        _mapRoot = result.Root;
        _selectedRow = null;
        RefreshTree();
        RefreshFiles();
        RefreshTypes();
        _status.Text = (result.Cancelled ? "Stopped: showing what was read so far. " : "")
                       + $"Read {Text.Plural(result.Root.FileCount, "file")} in {Text.Plural(result.Root.DirCount, "folder")} in {result.Elapsed.TotalSeconds:0.0} s"
                       + (result.LinksSkipped > 0 ? $"  ·  {Text.Plural(result.LinksSkipped, "junction or folder link")} not followed" : "") + ".";
        if (result.UnreadableCount > 0)
        {
            var text = $"{Text.Plural(result.UnreadableCount, "folder")} couldn't be read" + (Elevation.IsElevated || _places.IsTest ? " (Windows keeps them locked)." : " (run as administrator).");
            if (Elevation.IsElevated || _places.IsTest || SelfTestMode) ShowNotice(text);
            else ShowNotice(text, action: "Restart as administrator", act: RestartElevated);
        }

        ShowPage(_page is AppPage.Home or AppPage.Waste ? AppPage.Folders : _page);
    }

    // ---------- Helpers ----------

    internal static void Start(string exe, string args)
    {
        if (SelfTest.Active) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch
        {
            /* nothing to open */
        }
    }

    internal static void OpenInExplorer(string path, bool select)
    {
        if (select) Start("explorer.exe", "/select," + Text.Quote(path));
        else Start("explorer.exe", Text.Quote(path));
    }

    /// <summary>Folders Windows, programs or a user can't do without: never offered for deleting from the tree.</summary>
    public bool IsProtected(string path)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\');
        if (Path.GetPathRoot(p)?.TrimEnd('\\').Equals(p, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (_result is { } r && r.Root.FullPath.TrimEnd('\\').Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
        if (Places.Inside(p, _places.Windows)) return true;
        string[] exact =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            _places.ProgramData, Path.Combine(_places.SystemRoot, "Users"),
        ];
        if (exact.Any(e => e.Length > 0 && e.TrimEnd('\\').Equals(p, StringComparison.OrdinalIgnoreCase))) return true;
        if (_places.Profiles.Any(u => u.Path.TrimEnd('\\').Equals(p, StringComparison.OrdinalIgnoreCase) || u.Local.Equals(p, StringComparison.OrdinalIgnoreCase)
                                      || u.Roaming.Equals(p, StringComparison.OrdinalIgnoreCase) || Path.Combine(u.Path, "AppData").Equals(p, StringComparison.OrdinalIgnoreCase)))
            return true;
        var name = Path.GetFileName(p);
        return name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Settings ----------

    private sealed class Settings
    {
        public int TreeSort { get; set; } = 2;
        public bool TreeDescending { get; set; } = true;

        private static string File => Path.Combine(Program.DataFolder, "settings.json");

        public static Settings Load()
        {
            try
            {
                return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new() : new();
            }
            catch
            {
                return new();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Program.DataFolder);
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this));
            }
            catch
            {
                /* next change tries again */
            }
        }
    }
}

/// <summary>A fixed drive and its space.</summary>
public sealed record DriveSpace(string Root, string Label, string Format, long Total, long Free)
{
    public string Title => Label.Length > 0 ? $"{Root.TrimEnd('\\')}  {Label}" : Root.TrimEnd('\\');

    public static List<DriveSpace> Fixed()
    {
        var list = new List<DriveSpace>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new DriveSpace(d.RootDirectory.FullName, d.VolumeLabel, d.DriveFormat, d.TotalSize, d.TotalFreeSpace));
            }
            catch
            {
                /* not ready */
            }
        }

        return list;
    }
}

/// <summary>What a confirm dialog asks: a title, what happens, the items, and the button's words.</summary>
public sealed record Confirm(string Title, string Message, IReadOnlyList<string> Lines, string Yes, bool Danger = true);
