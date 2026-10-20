using System.Net.Http;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using TidyWin.Core.Insights;
using TidyWin.Core.Localization;
using TidyWin.Core.Security;
using TidyWin.Platform;
using Windows.Graphics;

namespace TidyWin.App;

public sealed partial class MainWindow : Window
{
    // The numbers are stable ids (screenshot scripts use them); the order of the buttons on screen is set in the XAML.
    private const int AiTab = 0, ModelsTab = 1, MemoryTab = 2, SecurityTab = 3, StatusTab = 4, SystemTab = 5, FinderTab = 6, StartupTab = 7, OverviewTab = 8, DiskTab = 9, SoftwareTab = 10, OptimizeTab = 11, EraseTab = 12;

    private readonly (Button Nav, UIElement Mark)[] _tabs;
    private readonly Func<UIElement>[] _factories;
    private readonly UIElement?[] _pages = new UIElement?[13];
    private readonly MemorySampler _sampler;
    private readonly ExitWatchService _exitWatch = null!;
    private readonly TrayIcon _tray = null!;
    private readonly Resident _resident = null!;
    private bool _exitDialog;
    private bool _exit;
    private int _current = -1;
    private bool _windowVisible = true;

    public MainWindow()
    {
        InitializeComponent();
        ShowLanguageLabel();

        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragBar);

        var icon = Path.Combine(AppContext.BaseDirectory, "TidyWin.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        AppWindow.Resize(new SizeInt32(1220, 900));
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 200, 210, 225);

        _tabs =
        [
            (AiNav, AiMark),
            (ModelsNav, ModelsMark),
            (MemNav, MemMark),
            (SecNav, SecMark),
            (StatusNav, StatusMark),
            (SysNav, SysMark),
            (FinderNav, FinderMark),
            (StartupNav, StartupMark),
            (OverviewNav, OverviewMark),
            (DiskNav, DiskMark),
            (SoftwareNav, SoftwareMark),
            (OptimizeNav, OptimizeMark),
            (EraseNav, EraseMark),
        ];

        // She needs about 860 px of height beside the thirteen buttons; in a shorter window she steps out of the way.
        SizeChanged += (_, e) => Dock.Visibility = e.Size.Height >= 860 ? Visibility.Visible : Visibility.Collapsed;

        _sampler = new MemorySampler(DispatcherQueue);
        _factories =
        [
            () => new AiCleanPage(CleanProfile.Ai),
            () => new ModelsPage(),
            () => new MemoryPage(_sampler),
            () => new SecurityPage(),
            () => new StatusPage(),
            () => new AiCleanPage(CleanProfile.System),
            () => new AiFilesPage(),
            () => new StartupPage(),
            () => new OverviewPage(() => Dock.InUse, Dock.Settings, GoTo, CanGo),
            () => new DiskPage(),
            () => new SoftwarePage(),
            () => new OptimizePage(),
            () => new ErasePage(),
        ];

        // Minimized means nobody is looking: sample memory rarely and give our own memory back.
        VisibilityChanged += (_, e) =>
        {
            _windowVisible = e.Visible;
            if (!e.Visible)
            {
                Page(_current)?.Deactivate();
                _sampler.SetCadence(MemorySampler.Minimized);
                Dock.Sleep();
                SelfTrim.Run();
            }
            else
            {
                Dock.Wake();
                _sampler.SetCadence(MemorySampler.Background); // the memory page raises it again in Activate
                Page(_current)?.Activate();
            }
        };

        _exitWatch = new ExitWatchService(new WindowsAiNetwork());
        _exitWatch.Updated += () => DispatcherQueue.TryEnqueue(ShowWatchBanner);
        _exitWatch.Alerted += (alert, ip, country) => DispatcherQueue.TryEnqueue(() => _ = WarnExitAsync(alert, ip, country));
        if (AppSettings.Load().WatchExit) _exitWatch.Start();
        _tray = new TrayIcon(ShowFromTray, ExitFromTray, () => AppSettings.Load().AutoMaintain, SetAutoMaintain);
        _resident = new Resident(_tray);
        _resident.Start();
        AppWindow.Closing += OnAppClosing;
        try
        {
            var show = EventWaitHandle.OpenExisting(@"Local\TidyWin.Show");
            ThreadPool.RegisterWaitForSingleObject(show, (_, _) => DispatcherQueue.TryEnqueue(ShowFromTray), null, Timeout.Infinite, false);
        }
        catch (WaitHandleCannotBeOpenedException) { }
        Closed += (_, _) =>
        {
            _resident.Dispose();
            _tray.Dispose();
            _exitWatch.Dispose();
        };

        // TIDYWIN_TAB=0..5 opens a given page directly (screenshots and tests).
        var start = int.TryParse(Environment.GetEnvironmentVariable("TIDYWIN_TAB"), out var s) && s >= 0 && s < _tabs.Length ? s : OverviewTab;
        Show(start);

        // Work out which AI tools are used a moment after the first page is up, off the UI thread.
        Dock.Wake();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Dock.Detect(greet: false));

        // The disk suggestion needs to know which AI tools are used, so it waits for the first look (once per run).
        Dock.Detected += OnFirstDetected;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CheckUpdateQuietly);
    }

    // ───────── the disk suggestion ─────────

    private Insight? _bannerInsight;

    private void OnFirstDetected()
    {
        Dock.Detected -= OnFirstDetected;

        // The overview may have been read before we knew which AI tools are used; read it again now that we do.
        if (_current == OverviewTab && _windowVisible) Page(OverviewTab)?.Activate();

        try { ShowDiskBanner(); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { /* a missing suggestion is never an error */ }
    }

    private void ShowDiskBanner()
    {
        var settings = Dock.Settings;
        var now = DateTime.UtcNow;
        var snap = SystemReader.Read();
        var used = snap.SystemDiskUsedPercent;
        var total = (long)snap.SystemDiskTotalBytes;
        var free = (long)snap.SystemDiskFreeBytes;

        // TIDYWIN_FAKE_DISK="94,12" pretends the system drive is 94% full with 12 GB free (screenshots and tests only).
        var fake = Environment.GetEnvironmentVariable("TIDYWIN_FAKE_DISK")?.Split(',');
        if (fake is { Length: 2 } && double.TryParse(fake[0], out var fu) && double.TryParse(fake[1], out var ff))
        {
            used = fu; free = (long)(ff * (1L << 30)); total = free == 0 ? total : (long)(free / Math.Max(0.01, (100 - used) / 100));
        }

        var facts = new Facts
        {
            NowUtc = now,
            DiskUsedPercent = used,
            DiskFreeBytes = free,
            DiskTotalBytes = total,
            AiToolsInUse = Dock.InUse,
            LastVisitUtc = settings.LastVisitUtc,
            DiskFreeAtLastVisit = settings.DiskFreeAtLastVisit,
        };

        // Only the disk-related suggestions belong in this strip, and only ones that lead somewhere.
        _bannerInsight = InsightEngine.Evaluate(facts, settings.ActiveSnoozes(now))
            .FirstOrDefault(i => i.Id is "disk.low" or "disk.shrinking" && TabFor(i.Go) is not null);

        // Remember today's free space for the next visit, but not on every launch: "since last time" needs a real gap.
        if (fake is null && (settings.LastVisitUtc is null || (now - settings.LastVisitUtc.Value).TotalHours >= 12))
        {
            settings.LastVisitUtc = now;
            settings.DiskFreeAtLastVisit = free;
            _ = Task.Run(() => settings.Save());
        }

        if (_bannerInsight is not { } insight) return;

        var urgent = insight.Level >= InsightLevel.Urgent;
        var accent = urgent ? Windows.UI.Color.FromArgb(255, 255, 122, 69) : Windows.UI.Color.FromArgb(255, 255, 184, 77);
        Banner.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, accent.R, accent.G, accent.B));
        Banner.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, accent.R, accent.G, accent.B));
        BannerIcon.Foreground = new SolidColorBrush(accent);
        BannerTitle.Text = insight.Title;
        BannerDetail.Text = insight.Detail;
        BannerGo.Content = insight.ActionLabel;
        UpdateBanner();
    }

    private bool _bannerDismissed;

    // The strip is for pages that do not already say it: not on the overview (it lists the same thing), and not on the
    // page the suggestion points to.
    private void UpdateBanner() =>
        Banner.Visibility = _bannerInsight is { } b && !_bannerDismissed && _current != OverviewTab && TabFor(b.Go) != _current
            ? Visibility.Visible : Visibility.Collapsed;

    private static int? TabFor(Destination d) => d switch
    {
        Destination.SystemClean => SystemTab,
        Destination.AiClean => AiTab,
        Destination.Models => ModelsTab,
        Destination.Finder => FinderTab,
        Destination.Startup => StartupTab,
        Destination.Disk => DiskTab,
        Destination.Software or Destination.Updates => SoftwareTab,
        Destination.Memory => MemoryTab,
        Destination.Security => SecurityTab,
        Destination.Status => StatusTab,
        _ => null, // pages that do not exist yet
    };

    private void OnBannerGo(object sender, RoutedEventArgs e)
    {
        if (_bannerInsight is { } i && TabFor(i.Go) is { } tab) Show(tab);
    }

    private void OnBannerLater(object sender, RoutedEventArgs e)
    {
        if (_bannerInsight is { } i)
        {
            var now = DateTime.UtcNow;
            Dock.Settings.Snooze(i.Id, now.AddDays(1), now);
            _ = Task.Run(() => Dock.Settings.Save());
        }
        _bannerDismissed = true;
        UpdateBanner();
    }

    private IPageLifecycle? Page(int index) => index >= 0 ? _pages[index] as IPageLifecycle : null;

    private void OnNavSystem(object sender, RoutedEventArgs e) => Show(SystemTab);
    private void OnNavAi(object sender, RoutedEventArgs e) => Show(AiTab);
    private void OnNavFinder(object sender, RoutedEventArgs e) => Show(FinderTab);
    private void OnNavStartup(object sender, RoutedEventArgs e) => Show(StartupTab);
    private void OnNavOverview(object sender, RoutedEventArgs e) => Show(OverviewTab);
    private void OnNavDisk(object sender, RoutedEventArgs e) => Show(DiskTab);
    private void OnNavSoftware(object sender, RoutedEventArgs e) => Show(SoftwareTab);
    private void OnNavOptimize(object sender, RoutedEventArgs e) => Show(OptimizeTab);
    private void OnNavErase(object sender, RoutedEventArgs e) => Show(EraseTab);

    // What a suggestion's button can do: open one of our pages, or (for the Recycle Bin) open the Windows folder itself,
    // so the person decides what leaves it.
    private bool CanGo(Destination d) => TabFor(d) is not null || d == Destination.Optimize;

    private void GoTo(Destination d)
    {
        if (d == Destination.Optimize)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
            return;
        }
        if (TabFor(d) is { } tab) Show(tab);
    }
    private void OnNavModels(object sender, RoutedEventArgs e) => Show(ModelsTab);

    // ───────── updates ─────────

    private readonly Updater _updater = new();
    private TidyWin.Core.Update.UpdateInfo? _update;
    private bool _updateBusy;

    /// <summary>Once a day at start-up, only when a feed is set and the person has not turned it off. It only looks; it never installs.</summary>
    private async void CheckUpdateQuietly()
    {
        try
        {
            var settings = AppSettings.Load();
            var feed = Updater.FeedUrl(settings);
            if (feed.Length == 0 || !settings.AutoCheckUpdates) return;
            if (settings.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(20)) { _updater.CleanOld(); return; }

            var result = await _updater.CheckAsync(feed, Updater.CurrentVersion(), TidyWin.Core.Localization.Loc.Language);
            settings = AppSettings.Load();
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            settings.Save();
            if (result.State == UpdateState.Available) MarkUpdateAvailable(result.Info!);
            else _updater.CleanOld();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A missing update notice is never an error.
        }
    }

    private void MarkUpdateAvailable(TidyWin.Core.Update.UpdateInfo info)
    {
        _update = info;
        var text = TidyWin.Core.Localization.Loc.F("新版本 {0}", info.Version);
        UpdateButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon { Glyph = "\uE896", FontSize = 13 },
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        UpdateButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0x35, 0xF2, 0xB3));
        ToolTipService.SetToolTip(UpdateButton, text);
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            if (_update is null)
            {
                var settings = AppSettings.Load();
                var feed = Updater.FeedUrl(settings);
                UpdateButton.IsEnabled = false;
                var result = await _updater.CheckAsync(feed, Updater.CurrentVersion(), TidyWin.Core.Localization.Loc.Language);
                UpdateButton.IsEnabled = true;
                settings.LastUpdateCheckUtc = DateTime.UtcNow;
                settings.Save();

                switch (result.State)
                {
                    case UpdateState.NotConfigured:
                        await Tell(TidyWin.Core.Localization.Loc.T("还没有设置更新来源"),
                            TidyWin.Core.Localization.Loc.F("安装包发布后,把更新地址(一个 https 链接,指向一个小的 JSON 文件)写进设置文件的 \"updateFeed\" 项,之后就能在这里检查更新。没有设置时,程序不会连接任何地方。\n\n设置文件:{0}", AppSettings.DefaultPath));
                        return;
                    case UpdateState.UpToDate:
                        await Tell(TidyWin.Core.Localization.Loc.T("已经是最新版本"),
                            TidyWin.Core.Localization.Loc.F("当前版本 {0}。", Updater.CurrentVersion()));
                        return;
                    case UpdateState.Failed:
                        await Tell(TidyWin.Core.Localization.Loc.T("没能检查更新"),
                            TidyWin.Core.Localization.Loc.F("原因:{0}。你的程序没有任何改动,稍后可以再试。", result.Problem ?? "?"));
                        return;
                    default:
                        MarkUpdateAvailable(result.Info!);
                        break;
                }
            }

            await OfferUpdate(_update!);
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private void OnAppClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exit || !_tray.Ready) return;
        args.Cancel = true;
        sender.Hide();
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        BringForward();
    }

    private void ExitFromTray()
    {
        _exit = true;
        Close();
    }

    private void SetAutoMaintain(bool on)
    {
        var settings = AppSettings.Load();
        settings.AutoMaintain = on;
        settings.Save();
    }

    private void ShowWatchBanner()
    {
        var bad = _exitWatch.Enabled && _exitWatch.State is ExitWatchState.Offline or ExitWatchState.China;
        WatchBanner.Visibility = bad ? Visibility.Visible : Visibility.Collapsed;
        if (bad) WatchText.Text = ExitWatch.Status(_exitWatch.State, _exitWatch.LastIp, _exitWatch.LastCountry);
    }

    private async Task WarnExitAsync(ExitAlert alert, string? ip, string? country)
    {
        ShowWatchBanner();
        if (alert == ExitAlert.None) return;
        var closed = await Task.Run(ClaudeStopper.Close);
        var head = alert == ExitAlert.Offline
            ? Loc.T("网络断了。")
            : Loc.F("网络出口变成中国了({0})。", ExitWatch.Where(ip, country));
        var tail = closed > 0
            ? Loc.F("已关掉 {0} 个 Claude 进程。等网络和 VPN 恢复后再打开。", closed)
            : Loc.T("没有发现正在运行的 Claude。等网络和 VPN 恢复后再打开。");
        var body = head + tail;
        ShowFromTray();
        _tray.Balloon(Loc.T("网络出问题了"), body);
        if (_exitDialog || Root.XamlRoot is null) return;
        _exitDialog = true;
        try { await Tell(Loc.T("网络出问题了"), body); }
        catch (Exception) { /* the red bar and the tray note stay up even if the dialog cannot open */ }
        finally { _exitDialog = false; }
    }

    private void BringForward()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();
        Activate();
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            FlashWindow(hwnd, true);
            MessageBeep(0x30);
        }
        catch (Exception)
        {
            // The reminder still shows in the window when the flash cannot be sent.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hwnd, bool invert);

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    private async Task Tell(string title, string body) =>
        await new ContentDialog { Title = title, Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap }, CloseButtonText = TidyWin.Core.Localization.Loc.T("知道了"), XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Dark }.ShowAsync();

    private async Task OfferUpdate(TidyWin.Core.Update.UpdateInfo info)
    {
        var notes = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = TidyWin.Core.Localization.Loc.F("当前版本 {0},新版本 {1}({2})。", Updater.CurrentVersion(), info.Version, InsightEngine.Size(info.Size))
                   + (info.Notes.Length > 0 ? "\n\n" + info.Notes : "")
                   + "\n\n" + TidyWin.Core.Localization.Loc.T("下载后会先核对校验值,通过了才安装;安装时程序会关闭并自动重新打开。你的设置和记录都会保留。"),
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel { Children = { notes, bar } };
        var dialog = new ContentDialog
        {
            Title = TidyWin.Core.Localization.Loc.T("有新版本"),
            Content = panel,
            PrimaryButtonText = TidyWin.Core.Localization.Loc.T("下载并安装"),
            CloseButtonText = TidyWin.Core.Localization.Loc.T("以后再说"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            args.Cancel = true; // stays open to show progress or the reason it failed
            try
            {
                if (TidyWin.Core.Safety.OperationGate.Shared.Current is { } running)
                {
                    notes.Text = TidyWin.Core.Safety.OperationGate.BusyMessage(running);
                    return;
                }
                dialog.IsPrimaryButtonEnabled = false;
                bar.Visibility = Visibility.Visible;
                var progress = new Progress<double>(p => bar.Value = p);
                var path = await _updater.DownloadAsync(info, progress);
                if (!Updater.Launch(path))
                {
                    notes.Text = TidyWin.Core.Localization.Loc.F("安装包已下载并核对无误,但没能自动启动。请手动运行:{0}", path);
                    return;
                }
                Application.Current.Exit();
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
            {
                bar.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
                notes.Text = TidyWin.Core.Localization.Loc.F("下载没有成功:{0}。没有安装任何东西,可以再试一次。", ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    // The button shows the language you would switch TO.
    private void ShowLanguageLabel() => LangButton.Content = TidyWin.Core.Localization.Loc.IsEnglish ? "中文" : "EN";

    private void OnLanguage(object sender, RoutedEventArgs e)
    {
        // Restarting would cut off a clean-up or uninstall half way, so wait until nothing is running.
        if (TidyWin.Core.Safety.OperationGate.Shared.Current is { } running)
        {
            Dock.PageChanged(new TidyWin.Core.Mascot.MascotState(TidyWin.Core.Mascot.Mood.Calm, TidyWin.Core.Safety.OperationGate.BusyMessage(running)));
            return;
        }

        var settings = AppSettings.Load();
        settings.Language = TidyWin.Core.Localization.Loc.IsEnglish ? "zh" : "en";
        if (!settings.Save()) return;

        try
        {
            if (Environment.ProcessPath is { } exe)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return; // the choice is saved; it shows the next time the program is opened
        }
        Application.Current.Exit();
    }
    private void OnNavMem(object sender, RoutedEventArgs e) => Show(MemoryTab);
    private void OnNavSecurity(object sender, RoutedEventArgs e) => Show(SecurityTab);
    private void OnNavStatus(object sender, RoutedEventArgs e) => Show(StatusTab);

    private void Show(int index)
    {
        if (index == _current) return;

        Page(_current)?.Deactivate();

        var created = _pages[index] is null;
        if (created)
        {
            var page = _factories[index]();
            _pages[index] = page;
            PageHost.Children.Add(page);

            // The mascot only follows the page that is on screen.
            if (page is IMascotSource source)
                source.Mascot.Changed += () => { if (_current == index) Dock.PageChanged(source.Mascot.State); };
        }

        for (var i = 0; i < _tabs.Length; i++)
        {
            var on = i == index;
            if (_pages[i] is { } page) page.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _tabs[i].Mark.Opacity = on ? 1 : 0;
            _tabs[i].Nav.Background = on
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 255, 255, 255))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        _current = index;
        // Arrived where the suggestion pointed: it has done its job for this session.
        if (_bannerInsight is { } arrived && TabFor(arrived.Go) == index) _bannerDismissed = true;
        UpdateBanner();
        if (_windowVisible) Page(index)?.Activate();
        if (_pages[index] is IMascotSource now) Dock.PageOpened(now.Mascot.State);
        if (!created && _pages[index] is { } shown) Motion.RiseIn(shown, 10, 220); // pages that already exist; new ones animate themselves
    }
}
