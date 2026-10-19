using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Erase;
using TidyWin.Core.Paths;
using TidyWin.Core.Safety;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

public enum ErasePhase { Idle, Scanning, Ready, Erasing, Done }

/// <summary>What one row stands for.</summary>
public enum EraseItemType { Files, Variable, Credential }

public sealed partial class EraseItemViewModel : ObservableObject
{
    public EraseItemViewModel(EraseScanItem scan, bool selected)
    {
        ItemType = EraseItemType.Files;
        Scan = scan;
        Title = scan.Location.Title;
        Detail = scan.Location.Detail;
        Where = scan.Path;
        SizeText = ByteSize.Format(scan.Bytes);
        CountText = scan.Location.IsFile ? Loc.T("1 个文件") : scan.FileCount == 0 ? Loc.T("空文件夹") : Loc.F("{0} 个文件",scan.FileCount);
        Newest = scan.NewestUtc;
        Bytes = scan.Bytes;
        IsSelected = selected;
    }

    public EraseItemViewModel(SecretVariable variable)
    {
        ItemType = EraseItemType.Variable;
        Variable = variable;
        Title = variable.Name;
        Detail = variable.Removable
            ? Loc.T("存在你账户的环境变量里,值不在这里显示。删掉后本机找不回这把密钥(要去服务商那里重新生成)。")
            : variable.InMachineScope
                ? Loc.T("是整台电脑的系统级变量,删它需要管理员权限,这里不动。")
                : Loc.T("只存在于启动这个程序的环境里,不是保存下来的变量,这里没法删。");
        Where = variable.InUserScope ? Loc.T("用户环境变量") : variable.InMachineScope ? Loc.T("系统环境变量") : Loc.T("当前进程");
        SizeText = "";
        CountText = Loc.T("密钥");
        IsSelected = variable.Removable;
    }

    public EraseItemViewModel(CredentialEntry credential)
    {
        ItemType = EraseItemType.Credential;
        Credential = credential;
        Title = credential.Target;
        Detail = Loc.T("Windows 凭据管理器里的一条登录信息。删掉后对应的程序需要重新登录。");
        Where = Loc.T("Windows 凭据管理器");
        SizeText = "";
        CountText = Loc.T("凭据");
        IsSelected = true;
    }

    public EraseItemType ItemType { get; }
    public EraseScanItem? Scan { get; }
    public SecretVariable? Variable { get; }
    public CredentialEntry? Credential { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Where { get; }
    public string SizeText { get; }
    public string CountText { get; }
    public DateTime? Newest { get; }
    public long Bytes { get; }
    public bool CanTick => ItemType != EraseItemType.Variable || Variable!.Removable;
    public string NewestText => Newest is { } d ? Loc.T("最近改动 ") + d.ToLocalTime().ToString("yyyy-MM-dd") : "";

    [ObservableProperty] private bool _isSelected;
}

public sealed class EraseGroupViewModel(string title, string note, IEnumerable<EraseItemViewModel> items)
{
    public string Title { get; } = title;
    public string Note { get; } = note;
    public ObservableCollection<EraseItemViewModel> Items { get; } = new(items);
    public string Header => $"{Title} · {Items.Count}";
}

public sealed partial class EraseToolCardViewModel(EraseTool tool) : ObservableObject
{
    public EraseTool Tool { get; } = tool;
    public string Name => Tool.Name;
    public string Summary => Tool.Summary;

    [ObservableProperty] private string _status = Loc.T("正在看…");
    [ObservableProperty] private bool _running;
}

/// <summary>
/// Removes everything one AI tool has left on this computer: conversations, login state, caches, keys in environment variables
/// and Windows' credential store. The person picks the tool, sees every place with its size, and nothing outside the listed
/// places can be touched: the guard is built from exactly the ticked items, and a tool that is running is never cleaned.
/// Settings the person wrote are listed but never ticked. The record of what was done keeps place names, never file names.
/// </summary>
public sealed partial class EraseViewModel : ObservableObject
{
    private readonly IEraseSystem _system;
    private readonly IAiEnvironment _env;
    private readonly IFileSystem _fs;
    private readonly IReparseProbe _probe;
    private readonly IManifestSink? _manifest;
    private readonly OperationGate _gate;
    private readonly Func<DateTime> _utcNow;

    public EraseViewModel(IEraseSystem system, IAiEnvironment env, IFileSystem fs, IReparseProbe probe,
        IManifestSink? manifest = null, OperationGate? gate = null, Func<DateTime>? utcNow = null)
    {
        _system = system;
        _env = env;
        _fs = fs;
        _probe = probe;
        _manifest = manifest;
        _gate = gate ?? new OperationGate();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        foreach (var t in AiErase.Tools) Cards.Add(new EraseToolCardViewModel(t));
    }

    public ObservableCollection<EraseToolCardViewModel> Cards { get; } = [];
    public ObservableCollection<EraseGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private ErasePhase _phase = ErasePhase.Idle;
    [ObservableProperty] private EraseTool? _tool;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultDetail = "";
    [ObservableProperty] private bool _nothingFound;

    public IReadOnlyList<string> BeforeSteps => Tool?.BeforeSteps ?? [];
    public IReadOnlyList<string> AfterSteps => Tool?.AfterSteps ?? [];

    private IEnumerable<EraseItemViewModel> AllItems => Groups.SelectMany(g => g.Items);
    public int SelectedCount => AllItems.Count(i => i.IsSelected && i.CanTick);
    public long SelectedBytes => AllItems.Where(i => i.IsSelected && i.CanTick).Sum(i => i.Bytes);
    public bool CanErase => Phase == ErasePhase.Ready && SelectedCount > 0 && !Running;

    public string SelectedText => SelectedCount == 0
        ? Loc.T("还没有勾选任何东西")
        : Loc.F("已勾选 {0} 项",SelectedCount) + (SelectedBytes > 0 ? $" · {ByteSize.Format(SelectedBytes)}" : "");

    // ---------------------------------------------------------------- the four tools, with what each has left

    public async Task LoadCardsAsync()
    {
        foreach (var card in Cards)
        {
            var tool = card.Tool;
            var found = await Task.Run(() =>
            {
                var scan = AiErase.Scan(tool, _env, _fs);
                var vars = _system.FindVariables(tool.Variables).Count(v => v.Removable);
                var creds = _system.FindCredentials(tool.CredentialKeywords).Count;
                var running = AiErase.ProcessesFor(tool, scan).Any(_system.IsRunning);
                return (Items: scan.Count, Bytes: scan.Sum(s => s.Bytes), vars, creds, running);
            });

            card.Running = found.running;
            var extra = found.vars + found.creds;
            card.Status = found.Items == 0 && extra == 0
                ? Loc.T("没有发现它留下的东西")
                : Loc.F("{0} 处 · {1}",found.Items,ByteSize.Format(found.Bytes)) + (extra > 0 ? Loc.F(" · {0} 个密钥/凭据",extra) : "");
            if (found.running) card.Status += Loc.T(" · 正在运行");
        }
    }

    // ---------------------------------------------------------------- one tool in detail

    public async Task ChooseAsync(string toolId)
    {
        if (Phase is ErasePhase.Scanning or ErasePhase.Erasing) return;
        var tool = AiErase.ById(toolId);
        if (tool is null) return;

        Phase = ErasePhase.Scanning;
        Tool = tool;
        Message = null;
        Groups.Clear();
        try
        {
            var scan = await Task.Run(() => AiErase.Scan(tool, _env, _fs));
            var vars = await Task.Run(() => _system.FindVariables(tool.Variables));
            var creds = await Task.Run(() => _system.FindCredentials(tool.CredentialKeywords));
            Running = await Task.Run(() => AiErase.ProcessesFor(tool, scan).Any(_system.IsRunning));

            AddFileGroup(scan, EraseKind.Conversations);
            AddFileGroup(scan, EraseKind.Credentials);
            if (vars.Count > 0)
                Groups.Add(new EraseGroupViewModel(Loc.T("环境变量里的密钥"), Loc.T("只显示变量名,不显示值。"), vars.Select(v => new EraseItemViewModel(v))));
            if (creds.Count > 0)
                Groups.Add(new EraseGroupViewModel(Loc.T("Windows 凭据管理器里的登录"), Loc.T("按名字匹配到的条目,逐条可取消。"), creds.Select(c => new EraseItemViewModel(c))));
            AddFileGroup(scan, EraseKind.CachesAndLogs);
            AddFileGroup(scan, EraseKind.Creations);
            AddFileGroup(scan, EraseKind.Settings);

            foreach (var item in AllItems) item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EraseItemViewModel.IsSelected)) RaiseSelection();
            };

            NothingFound = Groups.Count == 0;
            Phase = ErasePhase.Ready;
            RaiseSelection();
            OnPropertyChanged(nameof(BeforeSteps));
            OnPropertyChanged(nameof(AfterSteps));
        }
        catch (Exception e)
        {
            Message = e.Message;
            Phase = ErasePhase.Idle;
        }
    }

    private void AddFileGroup(IReadOnlyList<EraseScanItem> scan, EraseKind kind)
    {
        var items = scan.Where(s => s.Location.Kind == kind)
            .OrderByDescending(s => s.Bytes)
            .Select(s => new EraseItemViewModel(s, AiErase.SelectedByDefault(kind)))
            .ToList();
        if (items.Count > 0) Groups.Add(new EraseGroupViewModel(AiErase.KindTitle(kind), AiErase.KindNote(kind), items));
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(CanErase));
    }

    partial void OnPhaseChanged(ErasePhase value) => OnPropertyChanged(nameof(CanErase));
    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(CanErase));

    public void SetAll(bool selected)
    {
        foreach (var i in AllItems.Where(i => i.CanTick)) i.IsSelected = selected;
    }

    public void Back()
    {
        if (Phase is ErasePhase.Scanning or ErasePhase.Erasing) return;
        Groups.Clear();
        Tool = null;
        Message = null;
        Phase = ErasePhase.Idle;
    }

    // ---------------------------------------------------------------- erasing

    public async Task EraseAsync(bool dryRun = false)
    {
        if (Phase != ErasePhase.Ready || Tool is not { } tool) return;
        var picked = AllItems.Where(i => i.IsSelected && i.CanTick).ToList();
        if (picked.Count == 0) return;

        // Deleting what a running program is using would either fail or corrupt it, so the whole tool waits until it is closed.
        var runningNow = await Task.Run(() => AiErase.ProcessesFor(tool, picked.Where(i => i.ItemType == EraseItemType.Files).Select(i => i.Scan!)).Any(_system.IsRunning));
        Running = runningNow;
        if (runningNow && !dryRun)
        {
            Message = Loc.F("{0} 还在运行。先把它完全关掉(包括托盘里的),再回来清除。",tool.Name);
            return;
        }

        using var lease = _gate.TryEnter(Loc.F("清除 {0} 的痕迹",tool.Name));
        if (lease is null) { Message = OperationGate.BusyMessage(_gate.Current); return; }

        Phase = ErasePhase.Erasing;
        Message = null;
        try
        {
            var scanItems = picked.Where(i => i.ItemType == EraseItemType.Files).Select(i => i.Scan!).ToList();
            var guard = AiErase.BuildGuard(scanItems, new CachingReparseProbe(_probe));

            // Walked again now, so a file written since the scan is not missed (and one removed since is not counted).
            var files = await Task.Run(() => scanItems.SelectMany(FilesOf).ToList());

            var deleted = new List<string>();
            var report = await Task.Run(() => CleanExecutor.Run(files, guard, _fs, dryRun, null, default, deleted));

            if (!dryRun)
            {
                await Task.Run(() =>
                {
                    foreach (var s in scanItems.Where(s => !s.Location.IsFile)) RemoveEmptyTree(s.Path);
                    if (AiErase.HomePath(tool, _env) is { } home) _fs.TryDeleteEmptyDirectory(home);
                });
            }

            var vars = picked.Where(i => i.ItemType == EraseItemType.Variable).Select(i => i.Variable!).ToList();
            var creds = picked.Where(i => i.ItemType == EraseItemType.Credential).Select(i => i.Credential!).ToList();
            var varsDone = dryRun ? vars.Count : await Task.Run(() => vars.Count(v => _system.RemoveUserVariable(v.Name)));
            var credsDone = dryRun ? creds.Count : await Task.Run(() => creds.Count(c => _system.RemoveCredential(c)));

            if (!dryRun && (report.Deleted > 0 || varsDone > 0 || credsDone > 0))
            {
                // Place names only: a list of file names would itself be a record of what the person did with the tool.
                var places = picked.Select(i => i.ItemType switch
                {
                    EraseItemType.Variable => Loc.T("环境变量 ") + i.Title,
                    EraseItemType.Credential => Loc.T("凭据 ") + i.Title,
                    _ => i.Title,
                }).ToList();
                _manifest?.Record(new ManifestEntry(_utcNow(), "erase-" + tool.Id, report.Deleted, report.BytesFreed, places, 0));
            }

            Summarize(tool, dryRun, report, varsDone, vars.Count, credsDone, creds.Count);
        }
        catch (Exception e)
        {
            Message = e.Message;
            ResultTitle = Loc.T("没有完成");
            ResultDetail = e.Message;
        }

        Phase = ErasePhase.Done;
    }

    private IEnumerable<FsFile> FilesOf(EraseScanItem s)
    {
        if (!s.Location.IsFile) return _fs.EnumerateFilesRecursive(s.Path);
        return _fs.GetFile(s.Path) is { } f ? new[] { f } : Array.Empty<FsFile>();
    }

    private void Summarize(EraseTool tool, bool dryRun, CleanReport report, int varsDone, int varsAsked, int credsDone, int credsAsked)
    {
        var parts = new List<string>();
        if (report.Deleted > 0) parts.Add(Loc.F("{0} 个文件({1})",report.Deleted,ByteSize.Format(report.BytesFreed)));
        if (varsAsked > 0) parts.Add(Loc.F("{0} 个环境变量",varsDone));
        if (credsAsked > 0) parts.Add(Loc.F("{0} 条凭据",credsDone));
        var what = parts.Count == 0 ? Loc.T("没有需要处理的东西") : string.Join(Loc.T("、"), parts);

        ResultTitle = dryRun ? Loc.F("预演:会清掉 {0}",what) : Loc.F("{0} 的痕迹清掉了:{1}",tool.Name,what);

        var notes = new List<string>();
        if (dryRun) notes.Add(Loc.T("这只是预演,没有改动任何东西。"));
        if (report.AlreadyGone > 0) notes.Add(Loc.F("{0} 个文件在清理前就已经不在了。",report.AlreadyGone));
        var inUse = report.Skipped.Count(s => s.Reason == SkipReason.InUse);
        var denied = report.Skipped.Count(s => s.Reason == SkipReason.AccessDenied);
        var guarded = report.Skipped.Count(s => s.Reason == SkipReason.Guarded);
        var failed = report.Skipped.Count(s => s.Reason == SkipReason.Failed);
        if (inUse > 0) notes.Add(Loc.F("{0} 个文件正被某个程序占用,没有删掉。关掉相关程序后再来一次。",inUse));
        if (denied > 0) notes.Add(Loc.F("{0} 个文件没有权限删除。",denied));
        if (guarded > 0) notes.Add(Loc.F("{0} 个文件因为安全检查没有动(比如是链接或在范围之外)。",guarded));
        if (failed > 0) notes.Add(Loc.F("{0} 个文件删除失败。",failed));
        if (varsDone < varsAsked) notes.Add(Loc.F("{0} 个环境变量没能删掉。",varsAsked - varsDone));
        if (credsDone < credsAsked) notes.Add(Loc.F("{0} 条凭据没能删掉。",credsAsked - credsDone));
        if (!dryRun) notes.Add(Loc.T("已打开的终端和程序还带着旧的环境变量,重开之后才会消失。"));
        ResultDetail = string.Join("\n", notes);
    }

    /// <summary>Removes folders that are empty, deepest first. Anything holding a file stays, so nothing is lost by it.</summary>
    private void RemoveEmptyTree(string dir)
    {
        foreach (var child in _fs.EnumerateDirectories(dir).ToList()) RemoveEmptyTree(child);
        _fs.TryDeleteEmptyDirectory(dir);
    }
}
