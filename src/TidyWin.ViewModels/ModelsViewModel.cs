using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Memory;
using TidyWin.Core.Models;
using TidyWin.Core.Paths;
using TidyWin.Core.Safety;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

public static class RelativeTime
{
    public static string Describe(DateTime updatedUtc, DateTime nowUtc)
    {
        var days = (int)(nowUtc - updatedUtc).TotalDays;
        return days switch
        {
            < 1 => Loc.T("今天"),
            < 31 => Loc.F("{0} 天前",days),
            < 365 => Loc.F("{0} 个月前",days / 30),
            _ => Loc.F("{0} 年前",days / 365),
        };
    }
}

public sealed partial class ModelItemViewModel : ObservableObject
{
    public ModelEntry Entry { get; }
    public long FreeBytes { get; }
    public bool InUse { get; }
    public string UpdatedText { get; }

    public ModelItemViewModel(ModelEntry entry, long freeBytes, bool inUse, DateTime now)
    {
        Entry = entry;
        FreeBytes = freeBytes;
        InUse = inUse;
        UpdatedText = RelativeTime.Describe(entry.UpdatedUtc, now);
    }

    public event EventHandler? SelectionChanged;

    public string Name => Entry.Name;
    public string Kind => Entry.Kind;
    public string SizeText => ByteSize.Format(FreeBytes);
    public bool CanSelect => !InUse && FreeBytes > 0;

    public string Detail
    {
        get
        {
            var bits = new List<string> { Loc.T("更新于 ") + UpdatedText };
            if (Entry.Note is { Length: > 0 }) bits.Add(Entry.Note);
            if (Entry.OwnBytes > FreeBytes) bits.Add(Loc.T("与其他模型共用的部分会保留"));
            if (InUse) bits.Add(Loc.T("正在被 Ollama 使用,无法删除"));
            return string.Join(" · ", bits);
        }
    }

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect) { IsSelected = false; return; }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly IFileSystem _fs;
    private readonly IAiEnvironment _env;
    private readonly IReparseProbe _probe;
    private readonly IOllama _ollama;
    private readonly Func<DateTime> _utcNow;
    private readonly long _minListed;

    private List<ModelEntry> _all = [];
    private List<ResolvedAiTarget> _roots = [];

    private readonly OperationGate _gate;
    private readonly IManifestSink? _manifest;

    public ModelsViewModel(IFileSystem fs, IAiEnvironment env, IReparseProbe probe, IOllama ollama,
        Func<DateTime>? utcNow = null, long minListedBytes = MinListedBytes, IManifestSink? manifest = null, OperationGate? gate = null)
    {
        _gate = gate ?? new OperationGate();
        _manifest = manifest;
        _minListed = minListedBytes;
        _fs = fs;
        _env = env;
        _probe = probe;
        _ollama = ollama;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public const long MinListedBytes = 1024 * 1024;

    public ObservableCollection<ModelItemViewModel> Ollama { get; } = [];
    public ObservableCollection<ModelItemViewModel> LmStudio { get; } = [];
    public ObservableCollection<ModelItemViewModel> HuggingFace { get; } = [];

    public IEnumerable<ModelItemViewModel> Everything => Ollama.Concat(LmStudio).Concat(HuggingFace);

    [ObservableProperty] private CleanPhase _phase = CleanPhase.Idle;
    [ObservableProperty] private long _selectedBytes;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private long _freedSoFar;
    [ObservableProperty] private CleanReport? _report;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _error;

    public bool HasAny => Ollama.Count + LmStudio.Count + HuggingFace.Count > 0;

    public async Task ScanAsync()
    {
        if (Phase is CleanPhase.Scanning or CleanPhase.Cleaning) return;
        Phase = CleanPhase.Scanning;
        Error = null; Notice = null; Report = null;

        try
        {
            var now = _utcNow();
            var loadedTask = _ollama.ListLoadedAsync();

            var (roots, entries) = await Task.Run(() =>
            {
                var resolved = AiCatalog.Resolve(_env).ToList();
                string? PathOf(string id) => resolved.FirstOrDefault(r => r.Target.Id == id)?.Path;

                var list = new List<ModelEntry>();
                if (PathOf("ollama.models") is { } o) list.AddRange(OllamaInventory.Scan(o, _fs));
                if (PathOf("lmstudio.models") is { } l) list.AddRange(LmStudioInventory.Scan(l, _fs));
                if (PathOf("hf.hub") is { } h) list.AddRange(HfInventory.Scan(h, _fs));
                return (resolved, list);
            });

            _roots = roots;
            _all = entries;
            var inUse = (await loadedTask)?.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        ?? new HashSet<string>();

            foreach (var item in Everything) item.SelectionChanged -= OnSelectionChanged;
            Ollama.Clear(); LmStudio.Clear(); HuggingFace.Clear();

            foreach (var e in entries)
            {
                var free = ModelPlanner.ExclusiveBytes(e, entries);
                // Leftover ref/snapshot stubs with no weights are not a model worth listing.
                if (e.OwnBytes < _minListed) continue;
                var used = e.Source == ModelSource.Ollama && inUse.Contains(e.Name);
                var vm = new ModelItemViewModel(e, free, used, now);
                vm.SelectionChanged += OnSelectionChanged;
                (e.Source switch
                {
                    ModelSource.Ollama => Ollama,
                    ModelSource.LmStudio => LmStudio,
                    _ => HuggingFace,
                }).Add(vm);
            }

            // "old versions" is a subset of its whole repository entry; counting both would show too much.
            TotalBytes = Everything.Where(i => !i.Entry.Id.StartsWith("hf-old:", StringComparison.Ordinal)).Sum(i => i.FreeBytes);
            OnPropertyChanged(nameof(HasAny));
            Recalculate();
            Phase = CleanPhase.Ready;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Phase = CleanPhase.Idle;
        }
    }

    public void ClearSelection()
    {
        foreach (var i in Everything) i.IsSelected = false;
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => Recalculate();

    private void Recalculate()
    {
        var chosen = Everything.Where(i => i.IsSelected).Select(i => i.Entry).ToList();
        SelectedCount = chosen.Count;
        SelectedBytes = ModelPlanner.Plan(chosen, _all).Bytes;
    }

    public async Task CleanAsync(bool dryRun = false)
    {
        if (Phase != CleanPhase.Ready) return;

        // Ask again right now: the model may have been loaded since the scan.
        var loaded = (await _ollama.ListLoadedAsync())?.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                     ?? new HashSet<string>();
        var skippedBusy = new List<string>();
        foreach (var item in Everything.Where(i => i.IsSelected && i.Entry.Source == ModelSource.Ollama && loaded.Contains(i.Name)).ToList())
        {
            skippedBusy.Add(item.Name);
            item.IsSelected = false;
        }

        var chosen = Everything.Where(i => i.IsSelected).Select(i => i.Entry).ToList();
        if (chosen.Count == 0)
        {
            Notice = skippedBusy.Count > 0 ? Loc.F("{0} 正在被 Ollama 使用,没有删除。",string.Join(Loc.T("、"), skippedBusy)) : null;
            return;
        }

        using var lease = _gate.TryEnter(Loc.T("删除本地模型"));
        if (lease is null)
        {
            Error = OperationGate.BusyMessage(_gate.Current);
            return;
        }

        Phase = CleanPhase.Cleaning;
        Progress = 0; FreedSoFar = 0; Error = null;

        try
        {
            var plan = ModelPlanner.Plan(chosen, _all);

            // The guard only trusts the model folders that exist, and nothing the catalog marks as never-touch.
            var guard = new PathGuard(
                AiCatalog.AllowedRoots(_roots.Where(r => r.Target.Id is "ollama.models" or "lmstudio.models" or "hf.hub")),
                AiCatalog.ProtectedPaths(_roots),
                new CachingReparseProbe(_probe));

            var ui = new Progress<CleanProgress>(p =>
            {
                Progress = p.Total == 0 ? 1 : (double)p.Processed / p.Total;
                FreedSoFar = p.BytesFreedSoFar;
            });
            var deleted = new List<string>();
            var report = await Task.Run(() => CleanExecutor.Run(plan.Files, guard, _fs, dryRun, new Throttle(ui), default, deleted));
            if (!dryRun && report.Deleted > 0) _manifest?.Record(CleanManifest.Create("models", report, deleted, _utcNow()));

            Report = report;
            Progress = 1;
            FreedSoFar = report.BytesFreed;
            Notice = skippedBusy.Count > 0 ? Loc.F("{0} 正在被 Ollama 使用,已跳过。",string.Join(Loc.T("、"), skippedBusy)) : null;
            Phase = CleanPhase.Done;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Phase = CleanPhase.Ready;
        }
    }

    private sealed class Throttle(IProgress<CleanProgress> inner) : IProgress<CleanProgress>
    {
        private long _last;
        public void Report(CleanProgress v)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (v.Processed < v.Total && _last != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_last, now) < TimeSpan.FromMilliseconds(60)) return;
            _last = now;
            inner.Report(v);
        }
    }
}
