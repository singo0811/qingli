using System.ComponentModel;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TidyWin.Core.Memory;
using TidyWin.Platform;
using TidyWin.ViewModels;
using TidyWin.Core.Localization;

namespace TidyWin.App;

public sealed partial class MemoryPage : UserControl, IPageLifecycle, IMascotSource
{
    public MemoryViewModel ViewModel { get; }
    public MascotSignal Mascot { get; } = new(new TidyWin.Core.Mascot.MascotState(TidyWin.Core.Mascot.Mood.Calm, Loc.T("我在看内存的使用情况……")));

    private readonly Tween _hero;
    private readonly MemorySampler _sampler;

    public MemoryPage(MemorySampler sampler)
    {
        _sampler = sampler;
        ViewModel = sampler.ViewModel;

        InitializeComponent();

        HeroNumber.Foreground = Palette.Horizontal(Colors.White, Palette.Mint);
        _hero = new Tween(DispatcherQueue, v =>
        {
            var (n, u) = ByteSize.Split((long)v);
            HeroNumber.Text = n;
            HeroUnit.Text = u;
        });

        ViewModel.PropertyChanged += OnChanged;
        ViewModel.Models.CollectionChanged += (_, _) => RenderCards();

        Render();
    }

    public async void Activate()
    {
        _sampler.SetCadence(MemorySampler.Watching);
        await _sampler.TickAsync();
    }

    public void Deactivate() => _sampler.SetCadence(MemorySampler.Background);

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoryViewModel.AvailableBytes)) _hero.To(ViewModel.AvailableBytes);
        Render();
    }

    private void Render()
    {
        var vm = ViewModel;
        var pct = vm.TotalBytes <= 0 ? 0 : (double)vm.UsedBytes / vm.TotalBytes * 100;
        HeroSub.Text = vm.TotalBytes <= 0
            ? ""
            : Loc.F("共 {0} · 已用 {1}({2:0}%) · 提交 {3:0}%",ByteSize.Format(vm.TotalBytes),ByteSize.Format(vm.UsedBytes),pct,vm.CommitPercent);

        var color = Palette.Pressure(vm.Pressure);
        PressureText.Text = vm.Pressure switch
        {
            MemoryPressure.Calm => Loc.T("内存宽裕"),
            MemoryPressure.Moderate => Loc.T("使用中等"),
            MemoryPressure.High => Loc.T("有些紧张"),
            _ => Loc.T("内存告急"),
        };
        PressureText.Foreground = new SolidColorBrush(color);
        PressureDot.Fill = new SolidColorBrush(color);
        PressureChip.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, color.R, color.G, color.B));
        PressureChip.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, color.R, color.G, color.B));

        if (vm.TotalBytes > 0)
        {
            UsedCol.Width = new GridLength(Math.Max(1, vm.UsedBytes), GridUnitType.Star);
            FreeCol.Width = new GridLength(Math.Max(1, vm.AvailableBytes), GridUnitType.Star);
            UsedBar.Background = Palette.Horizontal(color, Windows.UI.Color.FromArgb(0xFF, (byte)(color.R * 0.6), (byte)(color.G * 0.6), (byte)(color.B * 0.8)));
        }

        TrimTitle.Text = vm.IdleCount > 0
            ? Loc.F("{0} 个程序在后台闲着,占着 {1}",vm.IdleCount,ByteSize.Format(vm.IdleBytes))
            : Loc.T("没有发现闲置的后台程序");
        TrimButtonText.Text = vm.Busy ? Loc.T("处理中…") : Loc.T("整理后台程序");
        TrimButton.IsEnabled = vm.CanTrimIdle;
        TrimResult.Text = DescribeTrim(vm.LastTrim) ??
            Loc.T("只会整理没在用的后台程序,让它们把暂时不用的内存还给系统。不会关闭它们,也不会丢数据。");

        NoticeText.Text = vm.Notice ?? "";
        NoticeText.Visibility = Palette.Vis(!string.IsNullOrEmpty(vm.Notice));

        AlertLines.Children.Clear();
        foreach (var a in vm.Alerts.Take(4))
        {
            AlertLines.Children.Add(new TextBlock
            {
                FontSize = 14,
                Foreground = Palette.Text,
                Text = Loc.F("{0}:{1:0} 分钟涨了 {2}(约每分钟 {3:0} MB)",a.Program,a.Minutes,ByteSize.Format(a.GrowthBytes),a.MegabytesPerMinute),
            });
        }
        AlertCard.Visibility = Palette.Vis(vm.Alerts.Count > 0);
        RenderCards();

        if (vm.TotalBytes > 0)
        {
            // The bubble's number is the same rounded figure the big number shows, so it does not flicker on every reading.
            Mascot.Set(vm.Busy
                ? TidyWin.Core.Mascot.MascotLines.MemoryTrimming()
                : TidyWin.Core.Mascot.MascotLines.Memory(vm.Pressure, ByteSize.Format(vm.AvailableBytes), vm.IdleCount, vm.Alerts.Count > 0));
        }
    }

    private void RenderCards() => OllamaCard.Visibility = Palette.Vis(ViewModel.Models.Count > 0);

    private static string? DescribeTrim(TrimReport? r)
    {
        if (r is null) return null;
        var tail = r.Denied > 0 ? Loc.F("{0} 个程序没有权限,已跳过。",r.Denied) : "";
        if (r.ProcessesTrimmed == 0) return Loc.T("没有整理成功任何程序。") + tail;

        var head = r.Delta >= 50L * 1024 * 1024
            ? Loc.F("实测可用内存增加了 {0}(整理了 {1} 个进程)。",ByteSize.Format(r.Delta),r.ProcessesTrimmed)
            : Loc.F("整理了 {0} 个进程,可用内存几乎没变。这说明它们占的内存系统本来就能随时拿回,不整理也不会拖慢你。",r.ProcessesTrimmed);
        return head + tail;
    }

    // ───────── actions ─────────

    private async void OnTrimIdle(object sender, RoutedEventArgs e) => await ViewModel.TrimIdleAsync();

    private async void OnTrimGroup(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row) await ViewModel.TrimGroupAsync(row);
    }

    private async void OnCloseGroup(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = Loc.F("关闭 {0}?",row.Name),
            Content = Loc.F("会像你点窗口右上角的 × 一样请求它关闭({0})。\n如果里面有没保存的内容,它会先问你,不会被强行结束。",row.CountText),
            PrimaryButtonText = Loc.T("关闭"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.CloseGroupAsync(row);
    }

    private async void OnUnload(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LoadedModelViewModel m)
            await ViewModel.UnloadModelAsync(m);
    }

    private static MemoryGroupViewModel? Row(object sender) => (sender as FrameworkElement)?.DataContext as MemoryGroupViewModel;
}
