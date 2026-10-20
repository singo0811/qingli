using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TidyWin.Core.Clean;
using TidyWin.Core.Security;
using TidyWin.Platform;
using TidyWin.ViewModels;
using Windows.UI;
using TidyWin.Core.Localization;

namespace TidyWin.App;

public sealed partial class SecurityPage : UserControl, IPageLifecycle, IMascotSource
{
    public SecurityViewModel ViewModel { get; }
    public MascotSignal Mascot { get; } = new(TidyWin.Core.Mascot.MascotLines.SecurityScanning());
    private bool _started;

    public AiNetworkViewModel NetVm { get; }

    public SecurityPage()
    {
        var net = new WindowsAiNetwork();
        ViewModel = new SecurityViewModel(new WindowsSecuritySource(), net);
        NetVm = new AiNetworkViewModel(net);
        InitializeComponent();
        NetProgress.Maximum = NetVm.Total;
        ViewModel.PropertyChanged += OnChanged;
        NetVm.PropertyChanged += OnNetChanged;
    }

    // The live check sends requests to other websites, so it only runs when the person presses the button.
    private async void OnCheckNet(object sender, RoutedEventArgs e) => await NetVm.CheckAsync();

    private void OnNetChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AiNetworkViewModel.Phase):
                var p = NetVm.Phase;
                NetButton.IsEnabled = p != NetPhase.Checking;
                NetButton.Content = p == NetPhase.Ready ? Loc.T("重新检查") : p == NetPhase.Checking ? Loc.T("检查中…") : Loc.T("开始检查");
                NetProgress.Visibility = Palette.Vis(p == NetPhase.Checking);
                NetResults.Visibility = Palette.Vis(p == NetPhase.Ready);
                if (p == NetPhase.Checking) Mascot.Set(TidyWin.Core.Mascot.MascotLines.AiNetChecking());
                if (p == NetPhase.Ready) Mascot.Set(TidyWin.Core.Mascot.MascotLines.AiNetResult(NetVm.Problems));
                if (p == NetPhase.Idle && NetVm.Error is { } err) NetSetup.Text = Loc.T("检查没能完成:") + err;
                break;
            case nameof(AiNetworkViewModel.Done):
                NetProgress.Value = NetVm.Done;
                break;
            case nameof(AiNetworkViewModel.Setup):
                NetSetup.Text = NetVm.Setup ?? "";
                break;
            case nameof(AiNetworkViewModel.ExitText):
                NetExit.Text = NetVm.ExitText ?? "";
                break;
            case nameof(AiNetworkViewModel.QualityText):
                NetQuality.Text = NetVm.QualityText ?? "";
                NetQuality.Visibility = string.IsNullOrEmpty(NetVm.QualityText) ? Visibility.Collapsed : Visibility.Visible;
                break;
            case nameof(AiNetworkViewModel.TraceText):
                NetTrace.Text = NetVm.TraceText ?? "";
                break;
        }
    }

    /// <summary>Checks once when first shown; after that only when asked.</summary>
    public async void Activate()
    {
        if (_started) return;
        _started = true;
        await ViewModel.ScanAsync();
    }

    public void Deactivate() { }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SecurityViewModel.Phase)) ApplyPhase();
    }

    private void ApplyPhase()
    {
        var p = ViewModel.Phase;
        ScanView.Visibility = Palette.Vis(p is CleanPhase.Idle or CleanPhase.Scanning);
        ReadyView.Visibility = Palette.Vis(p == CleanPhase.Ready);

        if (p == CleanPhase.Idle && ViewModel.Error is { } err) ScanTitle.Text = Loc.T("检查没能完成:") + err;
        else if (p == CleanPhase.Scanning) ScanTitle.Text = Loc.T("正在检查系统防护和网络暴露");
        if (p == CleanPhase.Ready) ShowReady();

        Mascot.Set(p switch
        {
            CleanPhase.Idle when ViewModel.Error is not null => TidyWin.Core.Mascot.MascotLines.Failed(Loc.T("检查")),
            CleanPhase.Ready => TidyWin.Core.Mascot.MascotLines.Security(ViewModel.RiskCount, ViewModel.WarnCount),
            _ => TidyWin.Core.Mascot.MascotLines.SecurityScanning(),
        });
    }

    private void ShowReady()
    {
        var vm = ViewModel;
        var color = Palette.Sev(vm.Worst);

        Headline.Text = vm.Headline;
        Headline.Foreground = new SolidColorBrush(color);

        // Halo and shield take the colour of the overall verdict.
        Halo.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0x70, color.R, color.G, color.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0x00, color.R, color.G, color.B), Offset = 1 },
            },
        };
        ShieldIcon.Foreground = Palette.Horizontal(Microsoft.UI.Colors.White, color);

        // The strip is drawn in proportion to what was found.
        RiskCol.Width = new GridLength(vm.RiskCount, GridUnitType.Star);
        WarnCol.Width = new GridLength(vm.WarnCount, GridUnitType.Star);
        InfoCol.Width = new GridLength(vm.InfoCount, GridUnitType.Star);
        OkCol.Width = new GridLength(vm.OkCount, GridUnitType.Star);
        if (vm.RiskCount + vm.WarnCount + vm.InfoCount + vm.OkCount == 0) OkCol.Width = new GridLength(1, GridUnitType.Star);

        Legend.Children.Clear();
        AddLegend(Loc.T("需要处理"), vm.RiskCount, Palette.Coral);
        AddLegend(Loc.T("建议留意"), vm.WarnCount, Palette.Amber);
        AddLegend(Loc.T("了解一下"), vm.InfoCount, Palette.Cyan);
        AddLegend(Loc.T("运行正常"), vm.OkCount, Palette.Mint);

        Section(RiskSection, RiskHeader, Loc.T("需要处理"), vm.Risks.Count);
        Section(WarnSection, WarnHeader, Loc.T("建议留意"), vm.Warns.Count);
        Section(InfoSection, InfoHeader, Loc.T("了解一下"), vm.Infos.Count);
        Section(OkSection, OkHeader, Loc.T("运行正常"), vm.Oks.Count);

        ExposedBlock.Visibility = Palette.Vis(vm.Exposed.Count > 0);
        ExposedTitle.Text = Loc.F("对外监听的程序 · {0}",vm.Exposed.Count);
        var more = vm.Exposed.Count - vm.ExposedShown.Count;
        ExposedMore.Text = more > 0 ? Loc.F("还有 {0} 个没有列出。",more) : "";
        ExposedMore.Visibility = Palette.Vis(more > 0);

        // Scroll back to the top once the new content has been laid out. Doing it before layout is silently ignored.
        ReadyView.UpdateLayout();
        ListScroll.ChangeView(null, 0, null, disableAnimation: true);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => ListScroll.ChangeView(null, 0, null, disableAnimation: true));

        // Animate the header and the whole list, never the children inside the ScrollViewer: those get clipped.
        Motion.Cascade(Hero, ListScroll);
    }

    private static void Section(StackPanel box, TextBlock header, string title, int count)
    {
        box.Visibility = Palette.Vis(count > 0);
        header.Text = $"{title} · {count}";
    }

    private void AddLegend(string text, int count, Color color)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = $"{text} {count}", FontSize = 13, Foreground = Palette.Dim });
        Legend.Children.Add(row);
    }

    // ───────── hover ─────────

    private void OnCardEnter(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && b.DataContext is FindingViewModel f)
        {
            var c = Palette.Sev(f.Severity);
            b.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, c.R, c.G, c.B));
        }
    }

    private void OnCardExit(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = new SolidColorBrush(Palette.Hex("1D2636"));
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await ViewModel.ScanAsync();
}
