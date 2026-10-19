using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TidyWin.Core.Clean;
using TidyWin.Core.Disk;
using TidyWin.Core.Mascot;
using TidyWin.Core.Safety;
using TidyWin.Platform;
using TidyWin.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using TidyWin.Core.Localization;

namespace TidyWin.App;

public sealed class DriveRow(DriveEntry d)
{
    public string Path { get; } = d.Name;
    public string Title { get; } = string.IsNullOrWhiteSpace(d.Label) ? d.Name.TrimEnd('\\') : $"{d.Name.TrimEnd('\\')}  {d.Label}";
    public double UsedPercent { get; } = d.TotalBytes == 0 ? 0 : (double)d.UsedBytes / d.TotalBytes * 100;
    public string Detail { get; } = Loc.F("已用 {0} / 共 {1} · 剩余 {2}",ByteSize.Format(d.UsedBytes),ByteSize.Format(d.TotalBytes),ByteSize.Format(d.FreeBytes));
}

public sealed class KnownRow(KnownSpaceHit h)
{
    public string Title { get; } = h.Spec.Title;
    public string About { get; } = h.Spec.About;
    public string HowTo { get; } = h.Spec.HowTo;
    public string Command { get; } = h.Spec.Command ?? "";
    public string SizeText { get; } = h.Bytes is { } b ? ByteSize.Format(b) : "";
}

/// <summary>One folder or file in the list. <see cref="WhyNot"/> is set when it must not be sent to the Recycle Bin.</summary>
public sealed class DiskRow
{
    // Plain get/set (not init/required): the XAML type info generator creates and fills these itself.
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sub { get; set; } = "";
    public long Bytes { get; set; }
    public double Percent { get; set; }
    internal DiskNode? Node { get; set; }
    internal FsFile? File { get; set; }
    public bool Informational { get; set; }
    public string? WhyNot { get; set; }

    public string SizeText => ByteSize.Format(Bytes);
    public string ShareText => Percent >= 1 ? $"{Percent:0}%" : "<1%";
    public bool CanEnter => Node is not null && !Informational;
    public bool CanReveal => !Informational;
    public bool CanRecycle => !Informational && WhyNot is null;
    public bool IsProtected => !Informational && WhyNot is not null;
}

/// <summary>Which folder holds the space. It only reads; the only thing it can do is send one item to the Recycle Bin, with a question first.</summary>
public sealed partial class DiskPage : UserControl, IPageLifecycle, IMascotSource
{
    public DiskViewModel ViewModel { get; }
    public MascotSignal Mascot { get; } = new(MascotLines.DiskIntro());

    public ObservableCollection<DriveRow> Drives { get; } = [];
    public ObservableCollection<KnownRow> Known { get; } = [];
    public ObservableCollection<DiskRow> Folders { get; } = [];
    public ObservableCollection<DiskRow> Files { get; } = [];

    private readonly Tween _hero;
    private Storyboard? _sweep;
    private string _lastPath = "";
    private bool _knownLoaded;

    public DiskPage()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] systemRoots =
        [
            windows,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];

        ViewModel = new DiskViewModel(
            new WindowsFileSystem(), new WindowsDrives(), new ShellRecycler(), new KnownSpaceFinder(),
            systemRoots.Where(s => s.Length > 0).ToList(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            manifest: new ManifestStore(), gate: OperationGate.Shared);

        InitializeComponent();
        ScanTrack.Clip = new RectangleGeometry { Rect = new Rect(0, 0, 440, 4) };
        HeroNumber.Foreground = Palette.Horizontal(Colors.White, Palette.Cyan);
        _hero = new Tween(DispatcherQueue, v =>
        {
            var (n, u) = ByteSize.Split((long)v);
            HeroNumber.Text = n;
            HeroUnit.Text = u;
        });

        ViewModel.PropertyChanged += OnChanged;
        ApplyPhase();
    }

    public async void Activate()
    {
        // Nothing is scanned until the person picks a disk; reading the drive list and the known big files is cheap.
        if (ViewModel.Phase == DiskPhase.Idle) LoadDrives();
        if (!_knownLoaded)
        {
            _knownLoaded = true;
            try
            {
                await ViewModel.LoadKnownAsync();
                Known.Clear();
                foreach (var h in ViewModel.KnownHits) Known.Add(new KnownRow(h));
                KnownPanel.Visibility = Palette.Vis(Known.Count > 0);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a missing hint is not an error */ }
        }
    }

    public void Deactivate() { }

    private void LoadDrives()
    {
        ViewModel.RefreshDrives();
        Drives.Clear();
        foreach (var d in ViewModel.DrivesNow) Drives.Add(new DriveRow(d));
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DiskViewModel.Phase):
                ApplyPhase();
                break;
            case nameof(DiskViewModel.ScannedFiles):
                ScanCount.Text = Loc.F("已看过 {0:N0} 个文件 · {1}",ViewModel.ScannedFiles,ByteSize.Format(ViewModel.ScannedBytes));
                break;
            case nameof(DiskViewModel.ScanningPath):
                ScanPath.Text = ViewModel.ScanningPath;
                ScanTitle.Text = Loc.T("正在分析 ") + ViewModel.ScanningPath.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                break;
            case nameof(DiskViewModel.Version):
                if (ViewModel.Phase == DiskPhase.Ready) ShowFolder();
                break;
            case nameof(DiskViewModel.Message):
                ShowMessage();
                break;
        }
    }

    private void ApplyPhase()
    {
        var p = ViewModel.Phase;
        IntroView.Visibility = Palette.Vis(p == DiskPhase.Idle);
        ScanView.Visibility = Palette.Vis(p == DiskPhase.Scanning);
        ReadyView.Visibility = Palette.Vis(p == DiskPhase.Ready);

        if (p == DiskPhase.Scanning) StartSweep(); else StopSweep();
        switch (p)
        {
            case DiskPhase.Idle:
                Mascot.Set(MascotLines.DiskIntro());
                LoadDrives();
                break;
            case DiskPhase.Scanning:
                Mascot.Set(MascotLines.DiskScanning());
                break;
            case DiskPhase.Ready:
                ShowFolder();
                break;
        }
    }

    private void ShowFolder()
    {
        var cur = ViewModel.Current;
        if (cur is null) return;

        var total = Math.Max(1, cur.Bytes);
        CrumbText.Text = cur.Path;
        UpButton.IsEnabled = ViewModel.CanGoUp;
        _hero.Jump(0);
        _hero.To(cur.Bytes);
        HeroSub.Text = Loc.F("{0:N0} 个文件 · {1} 个文件夹",cur.FileCount,cur.Folders.Count)
                       + (ViewModel.Scan is { Cancelled: true } ? Loc.T(" · 分析被中途停止,这只是已看过的部分") : "");

        Folders.Clear();
        foreach (var f in cur.Folders.OrderByDescending(f => f.Bytes).Take(40))
        {
            Folders.Add(new DiskRow
            {
                Name = f.Name,
                Path = f.Path,
                Bytes = f.Bytes,
                Percent = (double)f.Bytes / total * 100,
                Node = f,
                Sub = Loc.F("{0:N0} 个文件",f.FileCount) + (f.HasHiddenDepth ? Loc.T(" · 点「打开」会重新细看一遍") : ""),
                WhyNot = ViewModel.WhyNot(f.Path),
            });
        }
        if (cur.LooseBytes > total / 50)
        {
            Folders.Add(new DiskRow
            {
                Name = Loc.T("(直接放在这一层的文件)"),
                Path = cur.Path,
                Bytes = cur.LooseBytes,
                Percent = (double)cur.LooseBytes / total * 100,
                Sub = Loc.T("不在任何子文件夹里的文件,见下面「最大的文件」"),
                Informational = true,
            });
        }
        FoldersTitle.Visibility = Palette.Vis(Folders.Count > 0);

        Files.Clear();
        foreach (var file in ViewModel.FilesHere())
        {
            Files.Add(new DiskRow
            {
                Name = System.IO.Path.GetFileName(file.Path),
                Path = file.Path,
                Bytes = file.Length,
                Percent = (double)file.Length / total * 100,
                File = file,
                Sub = file.Path,
                WhyNot = ViewModel.WhyNot(file.Path),
            });
        }
        FilesTitle.Visibility = Palette.Vis(Files.Count > 0);

        ShowMessage();

        if (cur.Path != _lastPath)
        {
            _lastPath = cur.Path;
            var top = Folders.FirstOrDefault(r => !r.Informational);
            Mascot.Set(top is null
                ? MascotLines.DiskEmpty(cur.Name)
                : MascotLines.DiskLook(cur.Name, top.Name, top.SizeText, (int)Math.Round(top.Percent)));
        }
    }

    private void ShowMessage()
    {
        var m = ViewModel.Message;
        MessageBar.Visibility = Palette.VisText(m);
        MessageText.Text = m ?? "";
        BinButton.Visibility = Palette.Vis(m is not null && m.Contains(Loc.T("回收站")));
    }

    private void StartSweep()
    {
        if (_sweep is not null) return;
        var anim = new DoubleAnimation
        {
            From = -170, To = 440, Duration = TimeSpan.FromMilliseconds(1300),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(anim, ScanShift);
        Storyboard.SetTargetProperty(anim, "X");
        _sweep = new Storyboard();
        _sweep.Children.Add(anim);
        _sweep.Begin();
    }

    private void StopSweep()
    {
        _sweep?.Stop();
        _sweep = null;
    }

    // ───────── actions ─────────

    private async void OnScanDrive(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) await ViewModel.ScanAsync(path);
    }

    private void OnStop(object sender, RoutedEventArgs e) => ViewModel.Cancel();
    private void OnUp(object sender, RoutedEventArgs e) => ViewModel.Up();
    private void OnHome(object sender, RoutedEventArgs e) => ViewModel.Home();

    private async void OnRescan(object sender, RoutedEventArgs e)
    {
        var root = ViewModel.Scan?.Root.Path;
        if (root is not null) await ViewModel.ScanAsync(root);
    }

    private void OnPickAnother(object sender, RoutedEventArgs e)
    {
        ViewModel.Cancel();
        ViewModel.ResetToDrives();
    }

    private async void OnEnter(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DiskRow { Node: { } node } }) await ViewModel.EnterAsync(node);
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DiskRow row }) return;
        var arg = row.File is not null ? $"/select,\"{row.Path}\"" : $"\"{row.Path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true });
    }

    private void OnOpenBin(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string cmd } || cmd.Length == 0) return;
        var pkg = new DataPackage();
        pkg.SetText(cmd);
        Clipboard.SetContent(pkg);
    }

    private async void OnRecycle(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DiskRow row } || row.WhyNot is not null) return;

        var kind = row.File is not null ? Loc.T("文件") : Loc.T("文件夹");

        // Program data is allowed to go, but the person should know whose it is.
        var programData = row.Path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)
            ? Loc.T("\n\n注意:它在 AppData 里,是某个软件自己的数据。移走后那个软件可能丢失设置、登录状态或历史记录;")
              + Loc.T("软件正在运行时,Windows 也可能不让移动。不确定就先别动。")
            : "";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = Loc.F("把这个{0}移到回收站?",kind),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = Loc.F("{0}\n大小 {1}\n\n只是移到回收站,不会彻底删除,可以随时还原。",row.Path,row.SizeText)
                       + Loc.T("但要清空回收站后,这些空间才会真正回来。\n\n如果不确定它是什么,请先点「显示」看看里面的内容。") + programData,
            },
            PrimaryButtonText = Loc.T("移到回收站"),
            CloseButtonText = Loc.T("再想想"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var ok = row.File is { } f ? await ViewModel.RecycleFileAsync(f) : await ViewModel.RecycleFolderAsync(row.Node!);
        if (ok) Mascot.Set(MascotLines.DiskMoved(row.SizeText));
    }
}
