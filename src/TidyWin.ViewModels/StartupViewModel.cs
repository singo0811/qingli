using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.Clean;
using TidyWin.Core.Safety;
using TidyWin.Core.Startup;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

public sealed partial class StartupItemViewModel(StartupEntry entry, StartupAdvice advice) : ObservableObject
{
    public StartupEntry Entry { get; private set; } = entry;
    public StartupAdvice Advice { get; } = advice;

    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Note => Advice.Note;
    public string KindLabel => Advice.KindLabel;
    public bool IsMachineWide => Entry.IsMachineWide;

    /// <summary>Only a missing target or something suspicious can be removed; everything else can only be switched off.</summary>
    public bool CanRemove => Advice.Kind is StartupKind.Dead or StartupKind.Suspicious;

    [ObservableProperty] private bool _isEnabled = entry.Enabled;

    internal void Apply(bool enabled)
    {
        Entry = Entry with { Enabled = enabled };
        IsEnabled = enabled;
    }
}

public sealed partial class StartupViewModel : ObservableObject
{
    private readonly IStartupSource _source;
    private readonly Func<string, bool>? _fileExists;
    private readonly OperationGate _gate;
    private readonly IManifestSink? _manifest;
    private readonly Func<DateTime> _utcNow;

    public StartupViewModel(IStartupSource source, Func<string, bool>? fileExists = null, OperationGate? gate = null,
        IManifestSink? manifest = null, Func<DateTime>? utcNow = null)
    {
        _source = source;
        _fileExists = fileExists;
        _gate = gate ?? new OperationGate();
        _manifest = manifest;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public ObservableCollection<StartupItemViewModel> Items { get; } = [];

    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private int _suggestedCount;
    [ObservableProperty] private int _deadCount;

    public async Task RefreshAsync()
    {
        Busy = true;
        try
        {
            var read = await Task.Run(() => _source.Read());
            var rows = await Task.Run(() => read
                .Select(e => new StartupItemViewModel(e, StartupAdvisor.Advise(e, _fileExists)))
                .OrderBy(i => Rank(i))
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());

            Items.Clear();
            foreach (var r in rows) Items.Add(r);
            Loaded = true;
            Recount();
        }
        finally { Busy = false; }
    }

    // Worth a look first, normal ones next, switched-off ones last.
    private static int Rank(StartupItemViewModel i) =>
        !i.IsEnabled ? 5 :
        i.Advice.Kind == StartupKind.Suspicious ? 0 :
        i.Advice.Kind == StartupKind.Dead ? 1 :
        i.Advice.Verdict == StartupVerdict.Optional ? 2 :
        i.Advice.Verdict == StartupVerdict.Keep ? 4 : 3;

    private void Recount()
    {
        EnabledCount = Items.Count(i => i.IsEnabled);
        DeadCount = Items.Count(i => i.CanRemove);
        SuggestedCount = Items.Count(IsSuggested);
    }

    // The same notion as StartupAdvisor.Droppable, minus the entries that are better removed than switched off.
    // Cloud sync, tools you installed on purpose and programs we do not recognise are never switched off in bulk.
    private static bool IsSuggested(StartupItemViewModel i) =>
        i.IsEnabled && i.Advice.Kind is StartupKind.Chat or StartupKind.Updater or StartupKind.Game;

    /// <summary>Switches one entry on or off. Returns false (and says why in <see cref="Message"/>) when Windows refused.</summary>
    public async Task<bool> SetEnabledAsync(StartupItemViewModel item, bool enabled)
    {
        if (item.IsEnabled == enabled) return true;
        using var lease = _gate.TryEnter(Loc.T("修改开机启动项"));
        if (lease is null) { Message = OperationGate.BusyMessage(_gate.Current); return false; }

        var error = await Task.Run(() => _source.SetEnabled(item.Entry, enabled));
        if (error is not null) { Message = error; return false; }

        item.Apply(enabled);
        Message = null;
        Recount();
        return true;
    }

    /// <summary>Turns off everything that is both switched on and judged optional. Reversible one by one.</summary>
    public async Task<int> DisableSuggestedAsync()
    {
        var todo = Items.Where(IsSuggested).ToList();
        var done = 0;
        foreach (var i in todo)
            if (await SetEnabledAsync(i, false)) done++;
        if (done > 0) Message = Loc.F("已关闭 {0} 项,随时可以再打开。",done);
        return done;
    }

    /// <summary>Deletes an entry that points to a program that no longer exists (or looks hostile).</summary>
    public async Task<bool> RemoveAsync(StartupItemViewModel item)
    {
        if (!item.CanRemove) return false;
        using var lease = _gate.TryEnter(Loc.T("删除开机启动项"));
        if (lease is null) { Message = OperationGate.BusyMessage(_gate.Current); return false; }

        var error = await Task.Run(() => _source.Remove(item.Entry));
        if (error is not null) { Message = error; return false; }

        Items.Remove(item);
        _manifest?.Record(new ManifestEntry(_utcNow(), "startup", 1, 0, [$"{item.Entry.Source}: {item.Name} -> {item.Command}"], 0));
        Message = null;
        Recount();
        return true;
    }
}
