using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Mascot;
using TidyWin.Core.Safety;
using TidyWin.Platform;
using TidyWin.ViewModels;
using Windows.Foundation;
using TidyWin.Core.Localization;

namespace TidyWin.App;

/// <summary>The page for "my disk is full of AI things and I do not know what is safe to remove".</summary>
public sealed partial class AiFilesPage : UserControl, IPageLifecycle, IMascotSource
{
    public AiFilesViewModel ViewModel { get; }
    public MascotSignal Mascot { get; } = new(MascotLines.FinderIntro());

    private readonly Tween _hero;
    private Storyboard? _sweep;

    public AiFilesPage()
    {
        var env = new ProcessAiEnvironment();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] systemRoots =
        [
            windows,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];

        ViewModel = new AiFilesViewModel(
            new WindowsFileSystem(), new WindowsDrives(), new ShellRecycler(),
            systemRoots.Where(s => s.Length > 0).ToList(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            managedRoots: () => AiCatalog.Resolve(env)
                .Where(r => r.Target.Id is "ollama.models" or "lmstudio.models" or "hf.hub" or "hf.datasets")
                .Select(r => r.Path).ToList(),
            isRunningUnder: RunningPrograms.IsRunningUnder,
            manifest: new ManifestStore(),
            gate: OperationGate.Shared,
            merger: new HardLinkMerger(),
            sameFile: HardLinkMerger.SameFile);

        InitializeComponent();
        ScanTrack.Clip = new RectangleGeometry { Rect = new Rect(0, 0, 440, 4) };

        HeroNumber.Foreground = Palette.Horizontal(Colors.White, Palette.Mint);
        _hero = new Tween(DispatcherQueue, v =>
        {
            var (n, u) = ByteSize.Split((long)v);
            HeroNumber.Text = n;
            HeroUnit.Text = u;
        });

        ViewModel.PropertyChanged += OnChanged;
        ApplyPhase();
    }

    // Nothing is looked for until the person asks: it walks every disk, so it must be a choice.
    public void Activate() { }
    public void Deactivate() { }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AiFilesViewModel.Phase):
                ApplyPhase();
                break;
            case nameof(AiFilesViewModel.ScannedFiles):
                ScanCount.Text = Loc.F("已看过 {0:N0} 个文件 · {1}",ViewModel.ScannedFiles,ByteSize.Format(ViewModel.ScannedBytes));
                break;
            case nameof(AiFilesViewModel.ScanningPath):
                ScanPath.Text = ViewModel.ScanningPath;
                break;
            case nameof(AiFilesViewModel.SelectedCount):
            case nameof(AiFilesViewModel.SelectedBytes):
                UpdateMoveButton();
                break;
            case nameof(AiFilesViewModel.Message):
                ShowMessage();
                break;
        }
    }

    private void ApplyPhase()
    {
        var p = ViewModel.Phase;
        IntroView.Visibility = Palette.Vis(p == FinderPhase.Idle);
        ScanView.Visibility = Palette.Vis(p == FinderPhase.Scanning);
        ReadyView.Visibility = Palette.Vis(p is FinderPhase.Ready or FinderPhase.Working);

        if (p == FinderPhase.Scanning) StartSweep(); else StopSweep();
        MoveButton.IsEnabled = p == FinderPhase.Ready && ViewModel.HasSelection;

        switch (p)
        {
            case FinderPhase.Ready:
                ShowReady();
                break;
            case FinderPhase.Scanning:
                Mascot.Set(MascotLines.FinderScanning());
                break;
            case FinderPhase.Idle:
                Mascot.Set(MascotLines.FinderIntro());
                break;
        }
    }

    private void ShowReady()
    {
        var items = ViewModel.Items;
        _hero.Jump(0);
        _hero.To(ViewModel.TotalBytes);

        var safeCount = items.Count(i => i.Safety == FoundSafety.Safe);
        HeroSub.Text = items.Count == 0
            ? Loc.T("没有发现需要看的东西")
            : Loc.F("{0} 项 · 其中 {1}({2} 项)有把握放心处理,其余需要你自己判断",items.Count,ByteSize.Format(ViewModel.SafeBytes),safeCount)
              + (ViewModel.ScanWasPartial ? Loc.T(" · 查找被中途停止,这只是已看过的部分") : "");
        EmptyText.Visibility = Palette.Vis(items.Count == 0);

        UpdateMoveButton();
        ShowMessage();

        Mascot.Set(items.Count == 0
            ? MascotLines.FinderNothing()
            : MascotLines.FinderFound(ByteSize.Format(ViewModel.TotalBytes), ByteSize.Format(ViewModel.SafeBytes), safeCount));
    }

    private void ShowMessage()
    {
        var m = ViewModel.Message;
        MessageBar.Visibility = Palette.VisText(m);
        MessageText.Text = m ?? "";
        BinButton.Visibility = Palette.Vis(m is not null && m.Contains(Loc.T("已移到回收站")));
        if (m is not null && (m.StartsWith(Loc.T("已合并")) || m.StartsWith(Loc.T("已移到回收站"))))
            Mascot.Set(MascotLines.FinderApplied(m.Split('\n')[0]));
    }

    private void UpdateMoveButton()
    {
        MoveButtonText.Text = ViewModel.HasSelection ? Loc.F("处理所选 · {0}",ByteSize.Format(ViewModel.SelectedBytes)) : Loc.T("处理所选");
        MoveButton.IsEnabled = ViewModel.Phase == FinderPhase.Ready && ViewModel.HasSelection;
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

    private async void OnStart(object sender, RoutedEventArgs e) => await ViewModel.ScanAsync();
    private void OnStop(object sender, RoutedEventArgs e) => ViewModel.Cancel();
    private void OnSelectSafe(object sender, RoutedEventArgs e) => ViewModel.SelectSafe();
    private void OnClear(object sender, RoutedEventArgs e) => ViewModel.ClearSelection();

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void OnOpenBin(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });

    private async void OnMove(object sender, RoutedEventArgs e)
    {
        var chosen = ViewModel.Items.Where(i => i.IsSelected && i.CanSelect).ToList();
        if (chosen.Count == 0) return;

        var merges = chosen.Where(i => i.Action == FoundAction.Merge).ToList();
        var moves = chosen.Where(i => i.Action != FoundAction.Merge).ToList();
        var parts = new List<string>();

        if (merges.Count > 0)
            parts.Add(Loc.F("合并 {0} 项(不删除任何东西,这些位置的文件都还在,用到它们的程序照常能用;合并前会逐字节核对内容一致):\n",merges.Count)
                      + string.Join("\n", merges.Select(i => $"·  {i.Name}   {i.SizeText}")));

        if (moves.Count > 0)
        {
            var careful = moves.Where(i => i.Safety == FoundSafety.Careful).ToList();
            var text = Loc.F("移到回收站 {0} 项(可以还原,但要清空回收站后空间才真正回来):\n",moves.Count)
                       + string.Join("\n", moves.Select(i => $"·  {i.Name}   {i.SizeText}"));
            if (careful.Count > 0)
                text += Loc.F("\n\n其中 {0} 项需要你自己判断,删掉后可能要重新下载或重装:\n",careful.Count)
                        + string.Join("\n", careful.Select(i => $"·  {i.Name}:{i.Headline}"));
            parts.Add(text);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = Loc.F("确认处理这 {0} 项?",chosen.Count),
            Content = new ScrollViewer
            {
                MaxHeight = 380,
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = string.Join("\n\n", parts) },
            },
            PrimaryButtonText = Loc.T("确认处理"),
            CloseButtonText = Loc.T("再想想"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await ViewModel.ApplySelectedAsync();
    }
}
