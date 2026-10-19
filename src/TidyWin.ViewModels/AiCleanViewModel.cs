using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Paths;
using TidyWin.Core.Safety;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

public enum CleanPhase { Idle, Scanning, Ready, Cleaning, Done }

public sealed record TierSlice(AiSafety Safety, long Bytes, string Label);

public sealed partial class AiCleanViewModel : ObservableObject
{
    private readonly IFileSystem _fs;
    private readonly IAiEnvironment _env;
    private readonly IReparseProbe _probe;
    private readonly Func<DateTime> _utcNow;
    private CancellationTokenSource? _cts;

    private readonly IReadOnlyList<AiTarget> _catalog;
    private readonly IManifestSink? _manifest;
    private readonly string _source;
    private readonly OperationGate _gate;

    /// <param name="catalog">Which locations this page looks at. The AI list by default; the system clean-up page passes its own.</param>
    /// <param name="manifest">Gets a record of every real clean-up, so the person can look back at what was deleted.</param>
    /// <param name="source">Short name used in the record and in the "busy" message.</param>
    /// <param name="gate">Shared by every page that changes the computer. A private one when not given, so tests do not interfere.</param>
    public AiCleanViewModel(IFileSystem fs, IAiEnvironment env, IReparseProbe probe, Func<DateTime>? utcNow = null,
        IReadOnlyList<AiTarget>? catalog = null, IManifestSink? manifest = null, string source = "ai", OperationGate? gate = null)
    {
        _fs = fs;
        _env = env;
        _probe = probe;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _catalog = catalog ?? AiCatalog.All;
        _manifest = manifest;
        _source = source;
        _gate = gate ?? new OperationGate();
    }

    /// <summary>Raised after a real (not preview) clean-up finished, with what it did.</summary>
    public event Action<CleanReport>? Cleaned;

    /// <summary>Deletable locations that exist and hold something, biggest first.</summary>
    public ObservableCollection<AiItemViewModel> Items { get; } = [];

    /// <summary>Locations we recognise and deliberately leave alone.</summary>
    public ObservableCollection<AiItemViewModel> Untouched { get; } = [];

    [ObservableProperty] private CleanPhase _phase = CleanPhase.Idle;
    [ObservableProperty] private long _selectedBytes;
    [ObservableProperty] private long _foundBytes;
    [ObservableProperty] private IReadOnlyList<TierSlice> _strata = [];
    [ObservableProperty] private double _progress;
    [ObservableProperty] private long _freedSoFar;
    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private CleanReport? _report;
    [ObservableProperty] private string? _error;

    public string SelectedText => ByteSize.Format(SelectedBytes);
    public bool HasSelection => SelectedBytes > 0;
    partial void OnSelectedBytesChanged(long value)
    {
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(HasSelection));
    }

    public async Task ScanAsync()
    {
        if (Phase is CleanPhase.Scanning or CleanPhase.Cleaning) return;
        Phase = CleanPhase.Scanning;
        Error = null;
        Report = null;

        try
        {
            var now = _utcNow();
            var found = await Task.Run(() =>
            {
                var resolved = AiCatalog.Resolve(_env, _catalog);
                var expanded = AiCatalog.ExpandWildcards(resolved, _fs);
                return AiScanner.Scan(expanded, _fs, now);
            });

            foreach (var old in Items) old.SelectionChanged -= OnItemSelectionChanged;
            Items.Clear();
            Untouched.Clear();

            foreach (var scan in found.Where(s => s.Exists && !s.Target.CanBeDeleted))
                Untouched.Add(new AiItemViewModel(scan));

            // Only places where a clean would really free something. A cache whose files are all too new
            // to touch stays out of the list instead of showing a card with "0 B".
            foreach (var scan in found.Where(s => s.Exists && s.Target.CanBeDeleted && s.EligibleBytes > 0)
                         .OrderByDescending(s => s.EligibleBytes))
            {
                var vm = new AiItemViewModel(scan);
                vm.SelectionChanged += OnItemSelectionChanged;
                Items.Add(vm);
            }

            FoundBytes = Items.Sum(i => i.Bytes);
            Strata = BuildStrata(Items);
            RecalculateSelection();
            Phase = CleanPhase.Ready;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Phase = CleanPhase.Idle;
        }
    }

    public void SelectAllSafe()
    {
        foreach (var i in Items) i.IsSelected = i.CanSelect && i.Safety == AiSafety.Regenerable;
    }

    public void ClearSelection()
    {
        foreach (var i in Items) i.IsSelected = false;
    }

    public void Cancel() => _cts?.Cancel();

    public async Task CleanAsync(bool dryRun = false)
    {
        if (Phase != CleanPhase.Ready) return;

        var chosen = Items.Where(i => i.IsSelected && i.CanSelect).ToList();
        if (chosen.Count == 0) return;

        using var lease = _gate.TryEnter(_source == "ai" ? Loc.T("清理 AI 缓存") : Loc.T("清理系统垃圾"));
        if (lease is null)
        {
            Error = OperationGate.BusyMessage(_gate.Current);
            return;
        }

        Phase = CleanPhase.Cleaning;
        Progress = 0;
        FreedSoFar = 0;
        CurrentPath = "";
        Error = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var files = chosen.SelectMany(i => i.Scan.Eligible).ToList();

            // The guard only trusts folders the user ticked. Everything else, even other AI folders, is outside it.
            var allResolved = Items.Concat(Untouched).Select(i => i.Scan.Resolved).ToList();
            var guard = new PathGuard(
                AiCatalog.AllowedRoots(chosen.Select(i => i.Scan.Resolved)),
                AiCatalog.ProtectedPaths(allResolved),
                new CachingReparseProbe(_probe));

            var ui = new Progress<CleanProgress>(p =>
            {
                Progress = p.Total == 0 ? 1 : (double)p.Processed / p.Total;
                FreedSoFar = p.BytesFreedSoFar;
                CurrentPath = p.CurrentPath;
            });
            var throttled = new ThrottledProgress(ui, TimeSpan.FromMilliseconds(60));

            var deleted = new List<string>();
            var report = await Task.Run(() => CleanExecutor.Run(files, guard, _fs, dryRun, throttled, ct, deleted), CancellationToken.None);

            if (!dryRun && report.Deleted > 0)
            {
                _manifest?.Record(CleanManifest.Create(_source, report, deleted, _utcNow()));
                Cleaned?.Invoke(report);
            }

            Report = report;
            Progress = 1;
            FreedSoFar = report.BytesFreed;
            Phase = CleanPhase.Done;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Phase = CleanPhase.Ready;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnItemSelectionChanged(object? sender, EventArgs e) => RecalculateSelection();

    private void RecalculateSelection() =>
        SelectedBytes = Items.Where(i => i.IsSelected).Sum(i => i.Bytes);

    private static IReadOnlyList<TierSlice> BuildStrata(IEnumerable<AiItemViewModel> items) =>
        items.GroupBy(i => i.Safety)
            .Select(g => new TierSlice(g.Key, g.Sum(i => i.Bytes), g.First().TierLabel))
            .Where(s => s.Bytes > 0)
            .OrderBy(s => s.Safety)
            .ToList();

    /// <summary>Forwards at most one report per interval, so a 200k-file clean doesn't flood the UI thread.</summary>
    private sealed class ThrottledProgress(IProgress<CleanProgress> inner, TimeSpan every) : IProgress<CleanProgress>
    {
        private long _lastTicks;

        public void Report(CleanProgress value)
        {
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastTicks);
            var final = value.Processed >= value.Total;
            if (!final && Stopwatch.GetElapsedTime(last, now) < every && last != 0) return;
            Interlocked.Exchange(ref _lastTicks, now);
            inner.Report(value);
        }
    }
}
