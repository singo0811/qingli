using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWin.Core.Insights;
using TidyWin.Core.Mascot;
using TidyWin.Platform;
using TidyWin.ViewModels;
using TidyWin.Core.Localization;

namespace TidyWin.App;

/// <summary>One suggestion as the page shows it.</summary>
public sealed class InsightRow(Insight insight, bool canGo)
{
    public Insight Insight { get; } = insight;
    public string Title => Insight.Title;
    public string Detail => Insight.Detail;
    public InsightLevel Level => Insight.Level;
    public string LevelText => Palette.LevelLabel(Insight.Level);
    public string ActionLabel => Insight.ActionLabel;
    public bool CanGo { get; } = canGo && insight.Go != Destination.None;

    /// <summary>"Everything is fine" is not something to snooze.</summary>
    public bool CanSnooze => Insight.Level != InsightLevel.Good;
}

/// <summary>
/// What is worth doing on this computer right now, in order. Everything shown here rests on something that was really
/// measured a moment ago; nothing is invented to fill the page.
/// </summary>
public sealed partial class OverviewPage : UserControl, IPageLifecycle, IMascotSource
{
    public ObservableCollection<InsightRow> Rows { get; } = [];
    public MascotSignal Mascot { get; } = new(new MascotState(Mood.Busy, Loc.T("我先看看你电脑现在的情况……")));

    private readonly Func<IReadOnlyList<AiTool>> _aiTools;
    private readonly AppSettings _settings;
    private readonly Action<Destination> _go;
    private readonly Func<Destination, bool> _canGo;

    private Facts? _facts;
    private int _generation;

    public OverviewPage(Func<IReadOnlyList<AiTool>> aiTools, AppSettings settings, Action<Destination> go, Func<Destination, bool> canGo)
    {
        _aiTools = aiTools;
        _settings = settings;
        _go = go;
        _canGo = canGo;
        InitializeComponent();
        HeroNumber.Foreground = Palette.Horizontal(Colors.White, Palette.Mint);
        Footnote.Text = Loc.T("这些建议只基于刚才实际读到的:磁盘、内存、开机启动、回收站、更新与重启、磁盘和设备健康。") +
                        Loc.T("软件更新、安全体检请到对应页面查看。");
    }

    public async void Activate()
    {
        var generation = ++_generation;
        var now = DateTime.UtcNow;
        var tools = _aiTools();

        try
        {
            var fast = await Task.Run(() => FactsCollector.CollectFast(_settings, tools, now));
            if (generation != _generation) return;
            _facts = fast;
            Render();

            // Disk health and devices need one PowerShell call, so they arrive a few seconds later.
            var full = await FactsCollector.WithCheckupAsync(fast);
            if (generation != _generation) return;
            _facts = full;
            Render();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Not being able to look is not a finding: say nothing rather than guess.
        }
    }

    public void Deactivate() => _generation++;

    private void Render()
    {
        if (_facts is not { } f) return;

        var list = InsightEngine.Evaluate(f, _settings.ActiveSnoozes(DateTime.UtcNow));
        Rows.Clear();
        foreach (var i in list) Rows.Add(new InsightRow(i, _canGo(i.Go)));

        var todo = list.Count(i => i.Level >= InsightLevel.Tip);
        var worst = list.Count > 0 ? list[0].Level : InsightLevel.Good;
        var color = Palette.Level(worst);

        HeroNumber.Text = todo.ToString();
        HeroNumber.Foreground = Palette.Horizontal(Colors.White, color);
        HeroUnit.Text = todo == 0 ? Loc.T("件事需要你操心") : Loc.T("件事值得看看");

        var free = InsightEngine.Size(f.DiskFreeBytes);
        HeroSub.Text = Loc.F("系统盘剩 {0} · 内存已用 {1:0}% · 已开机 {2}",free,f.MemoryPercent,(f.UptimeDays < 1 ? Loc.F("{0:0} 小时",f.UptimeDays * 24) : Loc.F("{0:0.#} 天",f.UptimeDays)));
        DiskText.Text = free;
        MemText.Text = $"{f.MemoryPercent:0}%";
        StartText.Text = f.StartupEnabled.ToString();
        UpText.Text = f.UptimeDays < 1 ? Loc.F("{0:0} 小时",f.UptimeDays * 24) : Loc.F("{0:0.#} 天",f.UptimeDays);

        if (list.Count > 0) Mascot.Set(InsightEngine.Say(list[0]));
    }

    private void OnGo(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InsightRow row }) _go(row.Insight.Go);
    }

    private void OnSnooze(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InsightRow row }) return;
        var now = DateTime.UtcNow;
        _settings.Snooze(row.Insight.Id, now.AddDays(1), now);
        _ = Task.Run(() => _settings.Save());
        Render();
    }
}
