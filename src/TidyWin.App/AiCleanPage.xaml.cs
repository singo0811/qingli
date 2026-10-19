using System.ComponentModel;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Mascot;
using TidyWin.Platform;
using TidyWin.ViewModels;
using Windows.Foundation;
using TidyWin.Core.Localization;

namespace TidyWin.App;

public sealed partial class AiCleanPage : UserControl, IPageLifecycle, IMascotSource
{
    public AiCleanViewModel ViewModel { get; }
    public MascotSignal Mascot { get; }

    private readonly CleanProfile _profile;
    private readonly Tween _hero;
    private readonly Tween _cleaning;
    private readonly Tween _done;
    private Storyboard? _sweep;

    public AiCleanPage(CleanProfile profile)
    {
        _profile = profile;
        Mascot = new MascotSignal(profile.Scanning);
        ViewModel = new AiCleanViewModel(new WindowsFileSystem(), new ProcessAiEnvironment(), new FileSystemReparseProbe(),
            catalog: profile.Catalog);
        InitializeComponent();
        ScanTitle.Text = profile.ScanTitle;
        EmptyText.Text = profile.EmptyText;

        var glow = Palette.Horizontal(Colors.White, Palette.Mint);
        HeroNumber.Foreground = glow;
        CleanNumber.Foreground = glow;
        DoneNumber.Foreground = glow;

        _hero = new Tween(DispatcherQueue, v => SetNumber(HeroNumber, HeroUnit, v));
        _cleaning = new Tween(DispatcherQueue, v => SetNumber(CleanNumber, CleanUnit, v));
        _done = new Tween(DispatcherQueue, v => SetNumber(DoneNumber, DoneUnit, v));

        ScanTrack.Clip = new RectangleGeometry { Rect = new Rect(0, 0, 440, 4) };
        ViewModel.PropertyChanged += OnViewModelChanged;

    }

    private bool _started;

    /// <summary>The first scan happens when the page is first shown, not when the app is built.</summary>
    public async void Activate()
    {
        if (_started) return;
        _started = true;
        ApplyPhase();
        await ViewModel.ScanAsync();
    }

    public void Deactivate() { }

    private static void SetNumber(TextBlock number, TextBlock unit, double bytes)
    {
        var (n, u) = ByteSize.Split((long)bytes);
        number.Text = n;
        unit.Text = u;
    }

    // ───────── view model → view ─────────

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AiCleanViewModel.Phase):
                ApplyPhase();
                break;
            case nameof(AiCleanViewModel.SelectedBytes):
                if (ViewModel.Phase == CleanPhase.Ready)
                {
                    _hero.To(ViewModel.SelectedBytes);
                    UpdateSubtitle();
                    UpdateCleanButton();
                }
                break;
            case nameof(AiCleanViewModel.FreedSoFar):
                _cleaning.To(ViewModel.FreedSoFar);
                break;
            case nameof(AiCleanViewModel.Progress):
                CleanBar.Value = ViewModel.Progress;
                break;
            case nameof(AiCleanViewModel.CurrentPath):
                CleanPath.Text = ViewModel.CurrentPath;
                break;
        }
    }

    private void ApplyPhase()
    {
        var phase = ViewModel.Phase;

        ScanView.Visibility = Palette.Vis(phase is CleanPhase.Idle or CleanPhase.Scanning);
        ReadyView.Visibility = Palette.Vis(phase == CleanPhase.Ready);
        CleaningView.Visibility = Palette.Vis(phase == CleanPhase.Cleaning);
        DoneView.Visibility = Palette.Vis(phase == CleanPhase.Done);

        if (phase == CleanPhase.Scanning) StartSweep(); else StopSweep();

        switch (phase)
        {
            case CleanPhase.Idle when ViewModel.Error is { } err:
                ScanTitle.Text = Loc.T("扫描没能完成:") + err;
                break;
            case CleanPhase.Scanning:
                ScanTitle.Text = _profile.ScanTitle;
                break;
            case CleanPhase.Ready:
                ShowReady();
                break;
            case CleanPhase.Cleaning:
                _cleaning.Jump(0);
                CleanBar.Value = 0;
                CleanPath.Text = "";
                break;
            case CleanPhase.Done:
                ShowDone();
                break;
        }

        UpdateMascot();
    }

    private void UpdateMascot()
    {
        var vm = ViewModel;
        Mascot.Set(vm.Phase switch
        {
            CleanPhase.Idle when vm.Error is not null => MascotLines.Failed(Loc.T("扫描")),
            CleanPhase.Ready when vm.Items.Count == 0 => MascotLines.AiNothing(),
            CleanPhase.Ready => MascotLines.AiFound(ByteSize.Format(vm.FoundBytes), vm.Items.Count(i => i.IsSelected)),
            CleanPhase.Cleaning => MascotLines.AiCleaning(),
            CleanPhase.Done when vm.Report is { } r => MascotLines.AiDone(ByteSize.Format(r.BytesFreed), r.DryRun),
            _ => _profile.Scanning,
        });
    }

    private void ShowReady()
    {
        HeroLabel.Text = Loc.T("本次可释放");
        _hero.Jump(0);
        _hero.To(ViewModel.SelectedBytes);
        UpdateSubtitle();
        UpdateCleanButton();
        DrawStrata();

        var nothing = ViewModel.Items.Count == 0;
        EmptyText.Visibility = Palette.Vis(nothing);
        UntouchedBlock.Visibility = Palette.Vis(ViewModel.Untouched.Count > 0);
    }

    private void UpdateSubtitle()
    {
        var chosen = ViewModel.Items.Count(i => i.IsSelected);
        HeroSub.Text = ViewModel.Items.Count == 0
            ? Loc.T("没有发现可清理的内容")
            : Loc.F("共发现 {0} · {1} 个位置 · 已选 {2} 项",ByteSize.Format(ViewModel.FoundBytes),ViewModel.Items.Count,chosen);
    }

    private void UpdateCleanButton()
    {
        var preview = PreviewCheck.IsChecked == true;
        CleanButtonText.Text = preview ? Loc.T("预览结果") : Loc.F("清理 {0}",ViewModel.SelectedText);
        CleanButton.IsEnabled = ViewModel.HasSelection;
    }

    private void DrawStrata()
    {
        StrataBar.Children.Clear();
        StrataBar.ColumnDefinitions.Clear();
        StrataLegend.Children.Clear();

        var slices = ViewModel.Strata;
        var total = slices.Sum(s => s.Bytes);
        if (total <= 0) return;

        for (var i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            StrataBar.ColumnDefinitions.Add(new ColumnDefinition
            {
                // A thin tier would vanish; give every tier at least 1.5% of the bar.
                Width = new GridLength(Math.Max(s.Bytes, total * 0.015), GridUnitType.Star),
            });

            var seg = new Border { CornerRadius = new CornerRadius(7), Background = Palette.Gradient(s.Safety) };
            Grid.SetColumn(seg, i);
            StrataBar.Children.Add(seg);
            GrowFromLeft(seg, delayMs: 110 * i);

            var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            legend.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Palette.Accent(s.Safety)), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock { Text = s.Label, FontSize = 13, Foreground = Palette.Text });
            legend.Children.Add(new TextBlock { Text = ByteSize.Format(s.Bytes), FontSize = 13, Foreground = Palette.Dim });
            StrataLegend.Children.Add(legend);
        }
    }

    private static void GrowFromLeft(UIElement element, int delayMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        visual.Scale = new Vector3(0, 1, 1);

        var anim = c.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(1f, 1f, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f)));
        anim.Duration = TimeSpan.FromMilliseconds(750);
        anim.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        visual.StartAnimation("Scale.X", anim);
    }

    private void ShowDone()
    {
        var r = ViewModel.Report;
        if (r is null) return;

        DoneLabel.Text = r.DryRun ? Loc.T("预览 · 本可释放") : Loc.T("已释放");
        _done.Jump(0);
        _done.To(r.BytesFreed);

        DoneDetail.Text = r.DryRun
            ? Loc.F("将处理 {0} 个文件。这只是预览,没有删除任何东西。",r.Deleted)
            : Loc.F("删除了 {0} 个文件,清掉 {1} 个空文件夹",r.Deleted,r.DirectoriesRemoved);

        var notes = new List<string>();
        if (r.Cancelled) notes.Add(Loc.T("已按你的要求中途停止。"));
        AddCount(notes, r, SkipReason.InUse, Loc.T("个文件正被其他程序使用,已跳过,关掉对应程序后再清一次即可"));
        AddCount(notes, r, SkipReason.Guarded, Loc.T("个文件没通过安全检查,没有删除"));
        AddCount(notes, r, SkipReason.AccessDenied, Loc.T("个文件没有权限删除"));
        AddCount(notes, r, SkipReason.Failed, Loc.T("个文件删除失败"));
        if (r.AlreadyGone > 0) notes.Add(Loc.F("{0} 个文件在清理前已经不存在。",r.AlreadyGone));
        DoneNote.Text = notes.Count == 0 ? Loc.T("没有任何文件被跳过。") : string.Join("\n", notes);
    }

    private static void AddCount(List<string> notes, CleanReport r, SkipReason reason, string text)
    {
        var n = r.Skipped.Count(s => s.Reason == reason);
        if (n > 0) notes.Add($"{n} {text}");
    }

    // ───────── scan sweep ─────────

    private void StartSweep()
    {
        if (_sweep is not null) return;
        var anim = new DoubleAnimation
        {
            From = -170,
            To = 440,
            Duration = TimeSpan.FromMilliseconds(1300),
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

    // ───────── user actions ─────────

    private void OnSelectSafe(object sender, RoutedEventArgs e) => ViewModel.SelectAllSafe();
    private void OnClearSelection(object sender, RoutedEventArgs e) => ViewModel.ClearSelection();
    private void OnModeChanged(object sender, RoutedEventArgs e) => UpdateCleanButton();
    private void OnCancel(object sender, RoutedEventArgs e) => ViewModel.Cancel();
    private async void OnRescan(object sender, RoutedEventArgs e) => await ViewModel.ScanAsync();

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        var preview = PreviewCheck.IsChecked == true;

        var download = ViewModel.Items.Where(i => i.IsSelected && i.Safety == AiSafety.ReDownloadable).ToList();
        if (!preview && download.Count > 0)
        {
            var lines = string.Join("\n", download.Select(i => $"·  {i.Title}   {i.BytesText}"));
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ElementTheme.Dark,
                Title = Loc.T("这些内容删掉后需要重新下载"),
                Content = lines + Loc.T("\n\n以后再用到时要联网重新下载,模型可能很大。确定要清理吗?"),
                PrimaryButtonText = Loc.T("仍然清理"),
                CloseButtonText = Loc.T("再想想"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        await ViewModel.CleanAsync(preview);
    }
}
