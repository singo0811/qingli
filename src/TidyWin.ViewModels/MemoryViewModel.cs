using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.Memory;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

/// <summary>What a trim really did, measured with two readings of free memory, not estimated.</summary>
public sealed record TrimReport(
    int ProcessesTrimmed,
    int Denied,
    int Gone,
    long AvailableBefore,
    long AvailableAfter)
{
    public long Delta => AvailableAfter - AvailableBefore;
}

public sealed partial class MemoryGroupViewModel : ObservableObject
{
    public string Name { get; }
    public MemoryGroupViewModel(ProcessGroup g) { Name = g.Name; Group = g; }

    [ObservableProperty] private ProcessGroup _group;

    public int Count => Group.Count;
    public string WorkingSetText => ByteSize.Format(Group.WorkingSetBytes);
    public string PrivateText => ByteSize.Format(Group.PrivateBytes);
    public string CountText => Group.Count > 1 ? Loc.F("{0} 个进程",Group.Count) : Loc.T("1 个进程");
    public string Title => Group.WindowTitle is { Length: > 0 } t && Group.Count == 1 ? t : "";
    public bool CanClose => Group.CanClose;
    public bool CanTrim => !Group.IsProtected && !Group.IsForeground && Group.WorkingSetBytes > 0;
    public bool IsProtected => Group.IsProtected;

    public string StateText { get; private set; } = "";
    public MemoryGroupState State { get; private set; }

    public void Update(ProcessGroup g, bool isCandidate)
    {
        Group = g;
        State = g.IsProtected ? MemoryGroupState.System
              : g.IsForeground ? MemoryGroupState.InUse
              : g.IsRecentlyUsed ? MemoryGroupState.RecentlyUsed
              : isCandidate ? MemoryGroupState.IdleBackground
              : g.HasWindow ? MemoryGroupState.BackgroundWindow
              : MemoryGroupState.Background;
        StateText = State switch
        {
            MemoryGroupState.System => Loc.T("系统"),
            MemoryGroupState.InUse => Loc.T("正在使用"),
            MemoryGroupState.RecentlyUsed => Loc.T("刚用过"),
            MemoryGroupState.IdleBackground => Loc.T("后台空闲"),
            MemoryGroupState.BackgroundWindow => Loc.T("后台窗口"),
            _ => Loc.T("后台运行"),
        };
        OnPropertyChanged(string.Empty);
    }
}

public enum MemoryGroupState { System, InUse, RecentlyUsed, IdleBackground, BackgroundWindow, Background }

public sealed partial class LoadedModelViewModel(LoadedModel model) : ObservableObject
{
    public LoadedModel Model { get; } = model;
    public string Name => Model.Name;
    public string SizeText => ByteSize.Format(Model.SizeBytes);
    public string Where => Model.GpuBytes <= 0 ? Loc.T("内存")
        : Model.GpuBytes >= Model.SizeBytes ? Loc.T("显存") : Loc.T("显存 + 内存");
}

public sealed partial class MemoryViewModel : ObservableObject
{
    private const int TopGroups = 14;

    private readonly IMemorySource _source;
    private readonly IProcessControl _control;
    private readonly IOllama _ollama;
    private readonly ProtectionPolicy _policy;
    private readonly MemoryOptions _options;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, Task> _delay;

    private readonly ActivityTracker _activity = new();
    private readonly GrowthDetector _growth = new();
    private MemorySnapshot? _previous;
    private MemoryAnalysis? _analysis;

    public MemoryViewModel(
        IMemorySource source, IProcessControl control, IOllama ollama,
        ProtectionPolicy policy, int cores,
        Func<DateTime>? now = null, Func<TimeSpan, Task>? delay = null)
    {
        _source = source;
        _control = control;
        _ollama = ollama;
        _policy = policy;
        _options = new MemoryOptions(Cores: Math.Max(1, cores));
        _now = now ?? (() => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public ObservableCollection<MemoryGroupViewModel> Groups { get; } = [];
    public ObservableCollection<LoadedModelViewModel> Models { get; } = [];

    [ObservableProperty] private MemoryPressure _pressure;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private long _availableBytes;
    [ObservableProperty] private long _cacheBytes;
    [ObservableProperty] private double _commitPercent;
    [ObservableProperty] private int _idleCount;
    [ObservableProperty] private long _idleBytes;
    [ObservableProperty] private IReadOnlyList<GrowthAlert> _alerts = [];
    [ObservableProperty] private bool _ollamaRunning;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private TrimReport? _lastTrim;
    [ObservableProperty] private string? _notice;

    public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);
    public bool CanTrimIdle => !Busy && IdleCount > 0;

    partial void OnTotalBytesChanged(long value) => OnPropertyChanged(nameof(UsedBytes));
    partial void OnAvailableBytesChanged(long value) => OnPropertyChanged(nameof(UsedBytes));
    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanTrimIdle));
    partial void OnIdleCountChanged(int value) => OnPropertyChanged(nameof(CanTrimIdle));

    // ───────── reading ─────────

    public async Task RefreshAsync()
    {
        var snap = await Task.Run(_source.Read);
        var modelsTask = _ollama.ListLoadedAsync();

        var fgName = snap.ForegroundPid is { } fg
            ? snap.Processes.FirstOrDefault(p => p.Pid == fg && !_policy.IsProtected(p))?.Name
            : null;
        _activity.Observe(snap.At, fgName is null ? null : ProtectionPolicy.StripExe(fgName));

        var analysis = MemoryAdvisor.Analyze(snap, _previous, _policy, _options, _activity);
        _previous = snap;
        _analysis = analysis;
        _growth.Add(snap.At, analysis.Groups);

        Pressure = analysis.Pressure;
        TotalBytes = snap.System.TotalBytes;
        AvailableBytes = snap.System.AvailableBytes;
        CacheBytes = snap.System.CacheBytes;
        CommitPercent = snap.System.CommitPercent;
        IdleCount = analysis.TrimCandidates.Count;
        IdleBytes = analysis.TrimCandidateBytes;
        Alerts = _growth.Alerts();

        SyncGroups(analysis);
        SyncModels(await modelsTask);
    }

    private void SyncGroups(MemoryAnalysis analysis)
    {
        var candidates = analysis.TrimCandidates.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var top = analysis.Groups.Take(TopGroups).ToList();

        foreach (var gone in Groups.Where(vm => top.All(g => !g.Name.Equals(vm.Name, StringComparison.OrdinalIgnoreCase))).ToList())
            Groups.Remove(gone);

        for (var i = 0; i < top.Count; i++)
        {
            var g = top[i];
            var existing = Groups.FirstOrDefault(vm => vm.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new MemoryGroupViewModel(g);
                Groups.Insert(Math.Min(i, Groups.Count), existing);
            }
            existing.Update(g, candidates.Contains(g.Name));

            var at = Groups.IndexOf(existing);
            if (at != i) Groups.Move(at, i);
        }
    }

    private void SyncModels(IReadOnlyList<LoadedModel>? loaded)
    {
        OllamaRunning = loaded is not null;
        Models.Clear();
        foreach (var m in loaded ?? []) Models.Add(new LoadedModelViewModel(m));
    }

    // ───────── acting ─────────

    /// <summary>Trims every program the advisor currently calls idle and reports the measured change in free memory.</summary>
    public async Task<TrimReport?> TrimIdleAsync()
    {
        if (Busy || _analysis is null || _analysis.TrimCandidates.Count == 0) return null;
        return await TrimAsync(_analysis.TrimCandidates);
    }

    public async Task<TrimReport?> TrimGroupAsync(MemoryGroupViewModel vm)
    {
        if (Busy || !vm.CanTrim) return null;
        // Look the group up again: the row might be stale, and the policy is checked once more here.
        var group = _analysis?.Groups.FirstOrDefault(g => g.Name.Equals(vm.Name, StringComparison.OrdinalIgnoreCase));
        if (group is null || group.IsProtected || group.IsForeground) return null;
        return await TrimAsync([group]);
    }

    private async Task<TrimReport> TrimAsync(IReadOnlyList<ProcessGroup> groups)
    {
        Busy = true;
        Notice = null;
        try
        {
            var before = (await Task.Run(_source.Read)).System.AvailableBytes;

            int trimmed = 0, denied = 0, gone = 0;
            await Task.Run(() =>
            {
                foreach (var pid in groups.Where(g => !g.IsProtected).SelectMany(g => g.Pids))
                {
                    switch (_control.Trim(pid))
                    {
                        case TrimOutcome.Trimmed: trimmed++; break;
                        case TrimOutcome.AccessDenied: denied++; break;
                        case TrimOutcome.Gone: gone++; break;
                    }
                }
            });

            await _delay(TimeSpan.FromMilliseconds(1200)); // let Windows settle before measuring
            var after = (await Task.Run(_source.Read)).System.AvailableBytes;

            var report = new TrimReport(trimmed, denied, gone, before, after);
            LastTrim = report;
            await RefreshAsync();
            return report;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Asks the program's windows to close the normal way. Returns how many processes accepted the request.</summary>
    public async Task<int> CloseGroupAsync(MemoryGroupViewModel vm)
    {
        if (Busy || !vm.CanClose) return 0;
        var group = _analysis?.Groups.FirstOrDefault(g => g.Name.Equals(vm.Name, StringComparison.OrdinalIgnoreCase));
        if (group is null || !group.CanClose) return 0;

        Busy = true;
        try
        {
            var accepted = await Task.Run(() => group.Pids.Count(pid => _control.RequestClose(pid)));
            Notice = accepted > 0
                ? Loc.F("已请求关闭 {0}。如果它有没保存的内容,会先问你。",group.Name)
                : Loc.F("{0} 没有响应关闭请求。",group.Name);
            await _delay(TimeSpan.FromMilliseconds(1500));
            await RefreshAsync();
            return accepted;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task<bool> UnloadModelAsync(LoadedModelViewModel model)
    {
        if (Busy) return false;
        Busy = true;
        try
        {
            var ok = await _ollama.UnloadAsync(model.Name);
            Notice = ok ? Loc.F("已让 Ollama 卸载 {0}。模型文件还在,下次用会重新载入。",model.Name) : Loc.F("卸载 {0} 失败。",model.Name);
            await _delay(TimeSpan.FromMilliseconds(800));
            await RefreshAsync();
            return ok;
        }
        finally
        {
            Busy = false;
        }
    }
}
