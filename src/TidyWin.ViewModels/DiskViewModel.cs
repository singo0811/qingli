using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.Clean;
using TidyWin.Core.Disk;
using TidyWin.Core.Paths;
using TidyWin.Core.Safety;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

public enum DiskPhase { Idle, Scanning, Ready }

/// <summary>
/// Where the space went. Moves things to the Recycle Bin (never deletes for good) and refuses system places.
/// Going into a folder deeper than the scan kept re-scans just that folder, so memory stays small on any disk.
/// </summary>
public sealed partial class DiskViewModel : ObservableObject
{
    private readonly IFileSystem _fs;
    private readonly IDrives _drives;
    private readonly IRecycler _recycler;
    private readonly IKnownSpaceReader _known;
    private readonly IReadOnlyList<string> _systemRoots;
    private readonly string _userProfile;
    private readonly IManifestSink? _manifest;
    private readonly OperationGate _gate;
    private readonly Func<DateTime> _utcNow;

    private readonly Stack<(DiskScan Scan, DiskNode Current)> _trail = new();
    private CancellationTokenSource? _cts;

    public DiskViewModel(IFileSystem fs, IDrives drives, IRecycler recycler, IKnownSpaceReader known,
        IReadOnlyList<string> systemRoots, string userProfile, IManifestSink? manifest = null,
        OperationGate? gate = null, Func<DateTime>? utcNow = null)
    {
        _fs = fs;
        _drives = drives;
        _recycler = recycler;
        _known = known;
        _systemRoots = systemRoots;
        _userProfile = userProfile;
        _manifest = manifest;
        _gate = gate ?? new OperationGate();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    [ObservableProperty] private DiskPhase _phase = DiskPhase.Idle;
    [ObservableProperty] private IReadOnlyList<DriveEntry> _drivesNow = [];
    [ObservableProperty] private DiskScan? _scan;
    [ObservableProperty] private DiskNode? _current;
    [ObservableProperty] private long _scannedFiles;
    [ObservableProperty] private long _scannedBytes;
    [ObservableProperty] private string _scanningPath = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private IReadOnlyList<KnownSpaceHit> _knownHits = [];

    /// <summary>Counts up whenever the shown folder or its numbers changed, so the page knows to redraw.</summary>
    [ObservableProperty] private int _version;

    public bool CanGoUp => _trail.Count > 0 || Current?.Parent is not null;

    public void RefreshDrives() => DrivesNow = _drives.Read();

    public async Task LoadKnownAsync() => KnownHits = await Task.Run(() => _known.Read());

    public void Cancel() => _cts?.Cancel();

    /// <summary>Back to choosing a disk. Forgets the scan, so memory is given back as well.</summary>
    public void ResetToDrives()
    {
        if (Phase == DiskPhase.Scanning) return; // stop it first; this is not a way to abandon a running scan
        _trail.Clear();
        Scan = null;
        Current = null;
        Message = null;
        Phase = DiskPhase.Idle;
        Bump();
    }

    public async Task ScanAsync(string path)
    {
        if (Phase == DiskPhase.Scanning) return;
        Phase = DiskPhase.Scanning;
        Message = null;
        ScannedFiles = 0; ScannedBytes = 0; ScanningPath = path;
        _trail.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var ui = new Progress<DiskProgress>(p => { ScannedFiles = p.Files; ScannedBytes = p.Bytes; ScanningPath = p.Current; });
            var scan = await Task.Run(() => DiskScanner.Scan(path, _fs, ui, ct), CancellationToken.None);
            Scan = scan;
            Current = scan.Root;
            if (scan.Cancelled) Message = Loc.T("扫描被中止了,下面只是已经看过的那部分。");
            Phase = DiskPhase.Ready;
            Bump();
        }
        catch (Exception e)
        {
            Message = e.Message;
            Phase = Scan is null ? DiskPhase.Idle : DiskPhase.Ready;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Opens a folder. If it is deeper than what the scan kept, only that folder is looked at again.</summary>
    public async Task EnterAsync(DiskNode node)
    {
        if (Scan is null || Phase != DiskPhase.Ready) return;

        if (!node.HasHiddenDepth)
        {
            Current = node;
            Bump();
            return;
        }

        // The scan only counted what lies below this folder. Look at it properly, as a scan of its own.
        var before = (Scan, Current: Current ?? Scan.Root);
        Phase = DiskPhase.Scanning;
        ScannedFiles = 0; ScannedBytes = 0; ScanningPath = node.Path;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var ui = new Progress<DiskProgress>(p => { ScannedFiles = p.Files; ScannedBytes = p.Bytes; ScanningPath = p.Current; });
            var deeper = await Task.Run(() => DiskScanner.Scan(node.Path, _fs, ui, ct), CancellationToken.None);
            if (deeper.Cancelled) { Message = Loc.T("已取消。"); return; }
            _trail.Push((before.Scan, node));
            Scan = deeper;
            Current = deeper.Root;
        }
        catch (Exception e) { Message = e.Message; }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Phase = DiskPhase.Ready;
            Bump();
        }
    }

    public void Up()
    {
        if (Current?.Parent is { } parent) Current = parent;
        else if (_trail.Count > 0)
        {
            var (scan, at) = _trail.Pop();
            Scan = scan;
            Current = at;
        }
        Bump();
    }

    public void Home()
    {
        while (_trail.Count > 1) _trail.Pop();
        if (_trail.Count == 1) { Scan = _trail.Pop().Scan; }
        Current = Scan?.Root;
        Bump();
    }

    /// <summary>The biggest files under the folder being shown.</summary>
    public IReadOnlyList<FsFile> FilesHere(int count = 12)
    {
        if (Scan is null || Current is null) return [];
        return Scan.TopFiles.Where(f => WindowsPath.IsSameOrUnder(f.Path, Current.Path)).Take(count).ToList();
    }

    /// <summary>The folder worth mentioning in a hint, once a scan exists.</summary>
    public DiskNode? BiggestActionable() =>
        Scan is null ? null : DiskInsights.BiggestActionable(_trail.Count > 0 ? _trail.Last().Scan : Scan, _systemRoots, _userProfile);

    /// <summary>Why this path must not be moved to the Recycle Bin, or null when it may be.</summary>
    public string? WhyNot(string path) => RecyclePolicy.Refuse(path, _systemRoots, _userProfile);

    public async Task<bool> RecycleFolderAsync(DiskNode node) => await RecycleAsync(node.Path, node.Bytes, node);

    public async Task<bool> RecycleFileAsync(FsFile file) => await RecycleAsync(file.Path, file.Length, null);

    private async Task<bool> RecycleAsync(string path, long bytes, DiskNode? folder)
    {
        if (WhyNot(path) is { } why) { Message = why; return false; }

        using var lease = _gate.TryEnter(Loc.T("移到回收站"));
        if (lease is null) { Message = OperationGate.BusyMessage(_gate.Current); return false; }

        var error = await Task.Run(() => _recycler.Recycle(path));
        if (error is not null) { Message = error; return false; }

        _manifest?.Record(new ManifestEntry(_utcNow(), "disk-recycle", 1, bytes, [path], 0));
        Forget(path, bytes, folder);
        Message = Loc.F("已移到回收站({0}),需要的话可以从回收站还原。",ByteSize.Format(bytes));
        return true;
    }

    // Takes the thing out of the numbers on screen, so the picture stays right without scanning again.
    private void Forget(string path, long bytes, DiskNode? folder)
    {
        if (Scan is null) return;

        if (folder?.Parent is { } parent)
        {
            parent.Folders.Remove(folder);
            for (var p = parent; p is not null; p = p.Parent) { p.Bytes = Math.Max(0, p.Bytes - folder.Bytes); p.FileCount = Math.Max(0, p.FileCount - folder.FileCount); }
        }
        else
        {
            // A file, or a folder inside the part the scan did not keep one by one.
            var holder = Deepest(Scan.Root, path);
            holder.LooseBytes = Math.Max(0, holder.LooseBytes - bytes);
            for (var p = holder; p is not null; p = p.Parent) p.Bytes = Math.Max(0, p.Bytes - bytes);
        }

        Scan = Scan with
        {
            TopFiles = Scan.TopFiles.Where(f => !WindowsPath.IsSameOrUnder(f.Path, path)).ToList(),
            TotalBytes = Math.Max(0, Scan.TotalBytes - bytes),
        };

        if (Current is { } c && WindowsPath.IsSameOrUnder(c.Path, path)) Current = c.Parent ?? Scan.Root;
        Bump();
    }

    private static DiskNode Deepest(DiskNode node, string path)
    {
        foreach (var c in node.Folders)
            if (WindowsPath.IsSameOrUnder(path, c.Path)) return Deepest(c, path);
        return node;
    }

    private void Bump()
    {
        OnPropertyChanged(nameof(CanGoUp));
        Version++;
    }
}
