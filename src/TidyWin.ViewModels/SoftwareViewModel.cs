using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.Clean;
using TidyWin.Core.Paths;
using TidyWin.Core.Safety;
using TidyWin.Core.Software;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

public enum SoftwarePhase { Idle, Loading, Ready, Uninstalling, Checking, Residuals, Cleaning, Done }

public enum SoftwareSort { Size, Name, Date }

public sealed class AppItemViewModel(InstalledApp app)
{
    public InstalledApp App { get; } = app;
    public string Name => App.Name;
    public string Publisher => App.Publisher;
    public string Version => App.Version;
    public string SizeText => App.SizeBytes is { } b ? ByteSize.Format(b) : "";
    public string DateText => App.InstalledOn is { } d ? d.ToString("yyyy-MM-dd") : "";
}

public sealed partial class ResidualItemViewModel(Residual residual) : ObservableObject
{
    public Residual Residual { get; } = residual;
    public string Path => Residual.Path;
    public string Why => Residual.Why;
    public string SizeText => ByteSize.Format(Residual.Bytes);
    // Never ticked for the person: these folders may hold settings or saves, and cleaning them is permanent.
    [ObservableProperty] private bool _isSelected;
}

public enum UpgradeState { Idle, Running, Done, Failed }

public sealed partial class UpgradeItemViewModel(WingetUpgrade upgrade) : ObservableObject
{
    public WingetUpgrade Upgrade { get; } = upgrade;
    [ObservableProperty] private UpgradeState _state = UpgradeState.Idle;

    public string Name => Upgrade.Name;
    public string VersionText => $"{Upgrade.Current}  →  {Upgrade.Available}";
    public bool CanUpgrade => State is UpgradeState.Idle or UpgradeState.Failed;
    public string ActionText => State switch
    {
        UpgradeState.Running => Loc.T("更新中…"),
        UpgradeState.Done => Loc.T("已更新"),
        UpgradeState.Failed => Loc.T("重试"),
        _ => Loc.T("更新"),
    };

    partial void OnStateChanged(UpgradeState value)
    {
        OnPropertyChanged(nameof(CanUpgrade));
        OnPropertyChanged(nameof(ActionText));
    }
}

public enum UpdatesPhase { NotAsked, Loading, Ready, Unavailable, Failed }

/// <summary>
/// Installed programs, uninstalling them with their own uninstaller, and then offering what is left behind.
/// Nothing here deletes a program's files directly; leftovers are only the folders that match the program's name,
/// shown first, ticked by the person, and still confined by <see cref="PathGuard"/> to exactly those folders.
/// </summary>
public sealed partial class SoftwareViewModel : ObservableObject
{
    private readonly ISoftwareSource _source;
    private readonly IUninstaller _uninstaller;
    private readonly IWinget _winget;
    private readonly IFileSystem _fs;
    private readonly IReparseProbe _probe;
    private readonly IReadOnlyList<string> _residualRoots;
    private readonly Func<string, bool>? _fileExists;
    private readonly IManifestSink? _manifest;
    private readonly OperationGate _gate;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _utcNow;

    private IReadOnlyList<InstalledApp> _all = [];

    public SoftwareViewModel(ISoftwareSource source, IUninstaller uninstaller, IWinget winget, IFileSystem fs,
        IReparseProbe probe, IReadOnlyList<string> residualRoots, Func<string, bool>? fileExists = null,
        IManifestSink? manifest = null, OperationGate? gate = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? utcNow = null)
    {
        _source = source;
        _uninstaller = uninstaller;
        _winget = winget;
        _fs = fs;
        _probe = probe;
        _residualRoots = residualRoots;
        _fileExists = fileExists;
        _manifest = manifest;
        _gate = gate ?? new OperationGate();
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>How many times, and how far apart, to look whether an uninstall has finished.</summary>
    public int MaxChecks { get; init; } = 45;
    public TimeSpan CheckEvery { get; init; } = TimeSpan.FromSeconds(2);

    public ObservableCollection<AppItemViewModel> Apps { get; } = [];
    public ObservableCollection<ResidualItemViewModel> Residuals { get; } = [];
    public ObservableCollection<UpgradeItemViewModel> Upgrades { get; } = [];

    [ObservableProperty] private SoftwarePhase _phase = SoftwarePhase.Idle;
    [ObservableProperty] private UpdatesPhase _updatesPhase = UpdatesPhase.NotAsked;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private SoftwareSort _sort = SoftwareSort.Size;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _subject;
    [ObservableProperty] private CleanReport? _report;
    [ObservableProperty] private int _total;

    public int UpgradeCount => Upgrades.Count(u => u.State != UpgradeState.Done);

    partial void OnQueryChanged(string value) => Filter();
    partial void OnSortChanged(SoftwareSort value) => Filter();

    public async Task LoadAsync()
    {
        if (Phase is SoftwarePhase.Loading or SoftwarePhase.Uninstalling or SoftwarePhase.Checking or SoftwarePhase.Cleaning) return;
        Phase = SoftwarePhase.Loading;
        try
        {
            _all = await Task.Run(() => SoftwareCatalog.Build(_source.Read()));
            Total = _all.Count;
            Filter();
        }
        catch (Exception e) { Message = e.Message; }
        Phase = SoftwarePhase.Ready;
    }

    private void Filter()
    {
        var q = Query.Trim();
        IEnumerable<InstalledApp> list = _all;
        if (q.Length > 0)
            list = list.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase));

        list = Sort switch
        {
            SoftwareSort.Name => list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            SoftwareSort.Date => list.OrderByDescending(a => a.InstalledOn ?? DateTime.MinValue).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            _ => list.OrderByDescending(a => a.SizeBytes ?? 0).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
        };

        Apps.Clear();
        foreach (var a in list) Apps.Add(new AppItemViewModel(a));
    }

    // ---------------------------------------------------------------- uninstall and what it leaves

    public async Task UninstallAsync(AppItemViewModel item)
    {
        if (Phase != SoftwarePhase.Ready) return;
        using var lease = _gate.TryEnter(Loc.F("卸载 {0}",item.Name));
        if (lease is null) { Message = OperationGate.BusyMessage(_gate.Current); return; }

        var app = item.App;
        Message = null;
        Subject = app.Name;
        Residuals.Clear();
        Report = null;
        Phase = SoftwarePhase.Uninstalling;

        try
        {
            var (exe, args) = SoftwareCatalog.UninstallCommand(app, _fileExists);
            var code = await _uninstaller.RunAsync(exe, args);
            if (code is null)
            {
                Message = Loc.F("没能启动「{0}」自带的卸载程序。",app.Name);
                Phase = SoftwarePhase.Ready;
                return;
            }

            Phase = SoftwarePhase.Checking;
            var gone = await WaitUntilGoneAsync(app);
            if (!gone)
            {
                Message = Loc.F("没有看到「{0}」被卸载。如果你在卸载窗口里点了取消,什么都没有改变;如果还在进行,稍后可以刷新列表。",app.Name);
                Phase = SoftwarePhase.Ready;
                return;
            }

            await ReloadAsync();
            var left = await Task.Run(() => ResidualFinder.Find(app, _all, _residualRoots, _fs));
            foreach (var r in left.OrderByDescending(r => r.Bytes)) Residuals.Add(new ResidualItemViewModel(r));

            if (Residuals.Count == 0)
            {
                Message = Loc.F("「{0}」已卸载,没有发现它留下的文件夹。",app.Name);
                Phase = SoftwarePhase.Done;
            }
            else Phase = SoftwarePhase.Residuals;
        }
        catch (Exception e)
        {
            Message = e.Message;
            Phase = SoftwarePhase.Ready;
        }
    }

    private async Task<bool> WaitUntilGoneAsync(InstalledApp app)
    {
        for (var i = 0; i < MaxChecks; i++)
        {
            var now = await Task.Run(() => SoftwareCatalog.Build(_source.Read()));
            if (!now.Any(a => a.Id == app.Id)) return true;
            if (i < MaxChecks - 1) await _delay(CheckEvery, CancellationToken.None);
        }
        return false;
    }

    private async Task ReloadAsync()
    {
        _all = await Task.Run(() => SoftwareCatalog.Build(_source.Read()));
        Total = _all.Count;
        Filter();
    }

    public void SkipResiduals()
    {
        Residuals.Clear();
        Phase = SoftwarePhase.Ready;
    }

    public void Dismiss()
    {
        Message = null;
        Phase = SoftwarePhase.Ready;
    }

    public async Task CleanResidualsAsync(bool dryRun = false)
    {
        if (Phase != SoftwarePhase.Residuals) return;

        var chosen = Residuals.Where(r => r.IsSelected).Select(r => r.Residual).ToList();
        if (chosen.Count == 0) return;

        using var lease = _gate.TryEnter(Loc.T("清理软件残留"));
        if (lease is null) { Message = OperationGate.BusyMessage(_gate.Current); return; }

        Phase = SoftwarePhase.Cleaning;
        try
        {
            // Defence in depth: whatever the list says, only folders that really sit below a program-folder root.
            var roots = _residualRoots.Select(WindowsPath.Normalize).Where(r => r is not null).Select(r => r!).ToList();
            var paths = chosen
                .Select(c => WindowsPath.Normalize(c.Path))
                .Where(p => p is not null && roots.Any(r => WindowsPath.IsStrictlyUnder(p, r)))
                .Select(p => p!)
                .ToList();

            var guard = new PathGuard(paths, [], new CachingReparseProbe(_probe));
            var files = await Task.Run(() => paths.SelectMany(p => _fs.EnumerateFilesRecursive(p)).ToList());
            var deleted = new List<string>();
            var report = await Task.Run(() => CleanExecutor.Run(files, guard, _fs, dryRun, null, default, deleted));

            if (!dryRun)
            {
                // The executor never removes the folder it was allowed into, so tidy up the empty shell it leaves.
                await Task.Run(() => { foreach (var p in paths) RemoveEmptyTree(p); });
                if (report.Deleted > 0)
                    _manifest?.Record(CleanManifest.Create("software-residual", report, deleted, _utcNow()));
            }

            Report = report;
            Message = dryRun ? null : Loc.F("清掉了 {0} 个文件,释放 {1}。",report.Deleted,ByteSize.Format(report.BytesFreed));
            Residuals.Clear();
        }
        catch (Exception e) { Message = e.Message; }
        Phase = SoftwarePhase.Done;
    }

    /// <summary>Removes folders that are empty, deepest first. Anything holding a file stays, so nothing is lost by it.</summary>
    private void RemoveEmptyTree(string dir)
    {
        foreach (var child in _fs.EnumerateDirectories(dir).ToList()) RemoveEmptyTree(child);
        _fs.TryDeleteEmptyDirectory(dir);
    }

    // ---------------------------------------------------------------- updates through winget

    public async Task LoadUpdatesAsync()
    {
        if (UpdatesPhase == UpdatesPhase.Loading) return;
        if (!_winget.IsAvailable) { UpdatesPhase = UpdatesPhase.Unavailable; return; }

        UpdatesPhase = UpdatesPhase.Loading;
        var list = await _winget.ListUpgradesAsync();
        Upgrades.Clear();
        if (list is null) { UpdatesPhase = UpdatesPhase.Failed; OnPropertyChanged(nameof(UpgradeCount)); return; }

        foreach (var u in list) Upgrades.Add(new UpgradeItemViewModel(u));
        UpdatesPhase = UpdatesPhase.Ready;
        OnPropertyChanged(nameof(UpgradeCount));
    }

    public async Task UpgradeAsync(UpgradeItemViewModel item)
    {
        if (item.State == UpgradeState.Running) return;
        using var lease = _gate.TryEnter(Loc.F("更新 {0}",item.Upgrade.Name));
        if (lease is null) { Message = OperationGate.BusyMessage(_gate.Current); return; }

        item.State = UpgradeState.Running;
        var ok = await _winget.UpgradeAsync(item.Upgrade.Id);
        item.State = ok ? UpgradeState.Done : UpgradeState.Failed;
        OnPropertyChanged(nameof(UpgradeCount));
    }
}
