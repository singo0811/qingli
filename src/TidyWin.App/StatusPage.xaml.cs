using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWin.Core.Status;
using TidyWin.Platform;
using TidyWin.ViewModels;
using TidyWin.Core.Localization;

namespace TidyWin.App;

public sealed partial class StatusPage : UserControl, IPageLifecycle, IMascotSource
{
    public MascotSignal Mascot { get; } = new(TidyWin.Core.Mascot.MascotLines.Health(HealthBand.Good));

    public StatusPage() => InitializeComponent();

    public void Activate() => Refresh();
    public void Deactivate() { }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        var snap = SystemReader.Read();
        // CPU, battery and pending-reboot are not measured yet, so they never cost points.
        var report = HealthScore.Assess(SystemReader.ToHealthInputs(snap, cpuPercent: 0));

        var color = report.Band switch
        {
            HealthBand.Excellent => Palette.Mint,
            HealthBand.Good => Palette.Cyan,
            HealthBand.Fair => Palette.Amber,
            _ => Palette.Coral,
        };

        Mascot.Set(TidyWin.Core.Mascot.MascotLines.Health(report.Band));
        ScoreText.Text = report.Score.ToString();
        ScoreText.Foreground = Palette.Horizontal(Microsoft.UI.Colors.White, color);
        BandText.Text = report.Band switch
        {
            HealthBand.Excellent => Loc.T("状态很好"),
            HealthBand.Good => Loc.T("状态良好"),
            HealthBand.Fair => Loc.T("需要留意"),
            _ => Loc.T("建议处理"),
        };
        BandText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

        MemText.Text = $"{snap.MemoryPercent:0}%";
        MemBar.Value = snap.MemoryPercent;
        DiskText.Text = $"{snap.SystemDiskUsedPercent:0}%";
        DiskBar.Value = snap.SystemDiskUsedPercent;
        DiskSub.Text = Loc.F("剩余 {0} / 共 {1}",ByteSize.Format((long)snap.SystemDiskFreeBytes),ByteSize.Format((long)snap.SystemDiskTotalBytes));
        UpText.Text = snap.UptimeDays < 1 ? Loc.F("{0:0} 小时",snap.UptimeDays * 24) : Loc.F("{0:0.0} 天",snap.UptimeDays);

        Reasons.Children.Clear();
        if (report.Deductions.Count == 0)
        {
            Reasons.Children.Add(Line(Loc.T("没有发现需要扣分的问题"), Palette.Dim));
            return;
        }
        foreach (var d in report.Deductions)
            Reasons.Children.Add(Line($"{Describe(d.Code)}   −{d.Points}", Palette.Text));
    }

    private static TextBlock Line(string text, Microsoft.UI.Xaml.Media.Brush brush) =>
        new() { Text = text, FontSize = 15, Foreground = brush };

    private static string Describe(string code) => code switch
    {
        "memory.critical" => Loc.T("内存几乎用满"),
        "memory.high" => Loc.T("内存占用偏高"),
        "disk.critical" => Loc.T("系统盘几乎写满"),
        "disk.high" => Loc.T("系统盘空间紧张"),
        "disk.watch" => Loc.T("系统盘空间在减少"),
        "cpu.sustained" => Loc.T("CPU 长时间高负载"),
        "battery.degraded" => Loc.T("电池损耗明显"),
        "battery.aging" => Loc.T("电池有老化"),
        "uptime.long" => Loc.T("很久没有重启"),
        "reboot.pending" => Loc.T("有待完成的重启"),
        _ => code,
    };
}
