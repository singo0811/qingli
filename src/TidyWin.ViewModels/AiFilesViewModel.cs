using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Disk;
using TidyWin.Core.Safety;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

public sealed partial class FoundItemViewModel(FoundItem item) : ObservableObject
{
    public FoundItem Item { get; } = item;

    public string Path => Item.Path;
    public string Name => Item.Kind == FoundKind.Model ? System.IO.Path.GetFileName(Item.Path) : System.IO.Path.GetFileName(Item.Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Item.Path;
    public string Where => System.IO.Path.GetDirectoryName(Item.Path.TrimEnd('\\')) ?? Item.Path;
    public string SizeText => ByteSize.Format(Item.Bytes);
    public string Owner => Item.Owner;
    public string Format => Item.Format;
    public string Headline => Item.Headline;
    public string IfDeleted => Item.IfDeleted;
    public FoundSafety Safety => Item.Safety;
    public bool CanSelect => Item.Safety != FoundSafety.InUse;
    public FoundAction Action => Item.Action;
    public string SameAs => Item.SameAs ?? "";

    /// <summary>What ticking this item will do. Merging deletes nothing; moving to the Recycle Bin can be undone.</summary>
    public string ActionLabel => Item.Action == FoundAction.Merge ? Loc.T("合并(不删除)") : Loc.T("移到回收站");

    /// <summary>When it was last changed on disk. This is when it was downloaded or installed, not when it was last used.</summary>
    public string WhenText => Item.NewestWriteUtc == default ? "" : Loc.T("放入于 ") + RelativeTime.Describe(Item.NewestWriteUtc, DateTime.UtcNow);

    [ObservableProperty] private bool _isSelected;
}

public enum FinderPhase { Idle, Scanning, Ready, Working }

/// <summary>
/// The "my disk is full of AI things and I do not know what is safe" page. Looks beyond the usual cache folders,
/// explains each find, ticks nothing by itself, and only ever moves things to the Recycle Bin so they can be put back.
/// </summary>
public sealed partial class AiFilesViewModel : ObservableObject
{
    private readonly IFileSystem _fs;
    private readonly IDrives _drives;
    private readonly IRecycler _recycler;
    private readonly IReadOnlyList<string> _systemRoots;
    private readonly string _userProfile;
    private readonly Func<IReadOnlyList<string>> _managedRoots;
    private readonly Func<string, bool> _isRunningUnder;
    private readonly IManifestSink? _manifest;
    private readonly OperationGate _gate;
    private readonly Func<DateTime> _utcNow;
    private readonly IFileMerger? _merger;
    private readonly Func<string, string, bool> _sameFile;
    private CancellationTokenSource? _cts;

    public AiFilesViewModel(IFileSystem fs, IDrives drives, IRecycler recycler, IReadOnlyList<string> systemRoots, string userProfile,
        Func<IReadOnlyList<string>>? managedRoots = null, Func<string, bool>? isRunningUnder = null,
        IManifestSink? manifest = null, OperationGate? gate = null, Func<DateTime>? utcNow = null,
        IFileMerger? merger = null, Func<string, string, bool>? sameFile = null)
    {
        _merger = merger;
        _sameFile = sameFile ?? ((_, _) => false);
        _fs = fs;
        _drives = drives;
        _recycler = recycler;
        _systemRoots = systemRoots;
        _userProfile = userProfile;
        _managedRoots = managedRoots ?? (() => []);
        _isRunningUnder = isRunningUnder ?? (_ => false);
        _manifest = manifest;
        _gate = gate ?? new OperationGate();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public ObservableCollection<FoundItemViewModel> Items { get; } = [];

    [ObservableProperty] private FinderPhase _phase = FinderPhase.Idle;
    [ObservableProperty] private long _scannedFiles;
    [ObservableProperty] private long _scannedBytes;
    [ObservableProperty] private string _scanningPath = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private long _safeBytes;
    [ObservableProperty] private long _selectedBytes;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _scanWasPartial;

    public bool HasSelection => SelectedCount > 0;
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));

    public void Cancel() => _cts?.Cancel();

    public async Task ScanAsync()
    {
        if (Phase is FinderPhase.Scanning or FinderPhase.Working) return;
        Phase = FinderPhase.Scanning;
        Message = null;
        ScannedFiles = 0; ScannedBytes = 0; ScanningPath = "";
        Items.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var ui = new Progress<DiskProgress>(p => { ScannedFiles = p.Files; ScannedBytes = p.Bytes; ScanningPath = p.Current; });
            var options = new FinderOptions
            {
                ExcludeUnder = _managedRoots(),
                IsRunningUnder = _isRunningUnder,
                SameFile = _sameFile,
                UserProfile = _userProfile,
            };

            var found = await Task.Run(() => AiFinder.Find(Roots(), _fs, options, ui, ct), CancellationToken.None);

            foreach (var f in found)
            {
                var vm = new FoundItemViewModel(f);
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FoundItemViewModel.IsSelected)) Recount(); };
                Items.Add(vm);
            }
            ScanWasPartial = ct.IsCancellationRequested;
            Recount();
            Phase = FinderPhase.Ready;
        }
        catch (Exception e)
        {
            Message = e.Message;
            Phase = FinderPhase.Idle;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Top-level folders of every disk, except the ones Windows and programs live in.</summary>
    private IEnumerable<string> Roots()
    {
        var skip = _systemRoots.ToList();
        foreach (var d in _drives.Read())
        {
            foreach (var dir in _fs.EnumerateDirectories(d.Name))
            {
                var name = System.IO.Path.GetFileName(dir.TrimEnd('\\'));
                if (name.StartsWith('$') || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Recovery", StringComparison.OrdinalIgnoreCase)) continue;
                if (skip.Any(s => TidyWin.Core.Paths.WindowsPath.IsSameOrUnder(dir, s))) continue;
                yield return dir;
            }
        }
    }

    private void Recount()
    {
        TotalBytes = Items.Sum(i => i.Item.Bytes);
        SafeBytes = AiFinder.SafeBytes(Items.Select(i => i.Item));
        var chosen = Items.Where(i => i.IsSelected).ToList();
        SelectedCount = chosen.Count;
        SelectedBytes = chosen.Sum(i => i.Item.Bytes);
    }

    /// <summary>Ticks exactly the ones with a concrete reason behind them. The person still presses the button.</summary>
    public void SelectSafe()
    {
        foreach (var i in Items) i.IsSelected = i.Safety == FoundSafety.Safe;
    }

    public void ClearSelection()
    {
        foreach (var i in Items) i.IsSelected = false;
    }

    /// <summary>
    /// Does to each ticked item what its label says: duplicates are merged (nothing disappears, space returns at once),
    /// everything else goes to the Recycle Bin (can be put back; space returns once the bin is emptied).
    /// </summary>
    public async Task ApplySelectedAsync()
    {
        if (Phase != FinderPhase.Ready) return;
        var chosen = Items.Where(i => i.IsSelected && i.CanSelect).ToList();
        if (chosen.Count == 0) return;

        using var lease = _gate.TryEnter(chosen.All(i => i.Action == FoundAction.Merge) ? Loc.T("合并重复文件") : Loc.T("整理 AI 大文件"));
        if (lease is null) { Message = OperationGate.BusyMessage(_gate.Current); return; }

        Phase = FinderPhase.Working;
        Message = null;
        var merged = new List<FoundItemViewModel>();
        var moved = new List<FoundItemViewModel>();
        var refused = new List<string>();
        try
        {
            foreach (var item in chosen)
            {
                // Looked at again right now: the program may have been started since the scan.
                if (_isRunningUnder(item.Path)) { refused.Add(Loc.F("{0}:程序正在运行",item.Name)); continue; }
                if (RecyclePolicy.Refuse(item.Path, _systemRoots, _userProfile) is { } why) { refused.Add($"{item.Name}:{why}"); continue; }

                if (item.Action == FoundAction.Merge)
                {
                    if (_merger is null || item.Item.SameAs is null) { refused.Add(Loc.F("{0}:这个版本不能合并",item.Name)); continue; }
                    var other = item.Item.SameAs;
                    var err = await Task.Run(() => _merger.Merge(item.Path, other));
                    if (err is null) merged.Add(item); else refused.Add($"{item.Name}:{err}");
                }
                else
                {
                    var err = await Task.Run(() => _recycler.Recycle(item.Path));
                    if (err is null) moved.Add(item); else refused.Add($"{item.Name}:{err}");
                }
            }
        }
        finally { Phase = FinderPhase.Ready; }

        foreach (var m in merged.Concat(moved)) Items.Remove(m);
        Recount();

        var lines = new List<string>();
        if (merged.Count > 0)
        {
            var bytes = merged.Sum(m => m.Item.Bytes);
            _manifest?.Record(new ManifestEntry(_utcNow(), "ai-finder-merge", merged.Count, bytes,
                merged.Select(m => $"{m.Path}  =>  {m.Item.SameAs}").ToList(), 0));
            lines.Add(Loc.F("已合并 {0} 项,马上腾出 {1}。原来的位置都还在,用到它们的程序照常能用。",merged.Count,ByteSize.Format(bytes)));
        }
        if (moved.Count > 0)
        {
            var bytes = moved.Sum(m => m.Item.Bytes);
            _manifest?.Record(new ManifestEntry(_utcNow(), "ai-finder", moved.Count, bytes, moved.Select(m => m.Path).ToList(), 0));
            lines.Add(Loc.F("已移到回收站 {0} 项({1})。空间要等清空回收站才会真正腾出来;在那之前,随时可以从回收站还原。",moved.Count,ByteSize.Format(bytes)));
        }
        if (refused.Count > 0) lines.Add(Loc.T("没有处理:") + string.Join(";", refused));
        Message = lines.Count == 0 ? null : string.Join("\n", lines);
    }
}
