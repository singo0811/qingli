using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.Optimize;
using TidyWin.Core.Safety;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

public enum OptimizeState { Idle, Running, Done, Failed }

public sealed partial class OptimizeItemViewModel(OptimizeTask task) : ObservableObject
{
    public OptimizeTask Task { get; } = task;
    [ObservableProperty] private OptimizeState _state = OptimizeState.Idle;
    [ObservableProperty] private string? _result;

    /// <summary>Extra line shown under the title, e.g. how much is in the Recycle Bin right now.</summary>
    [ObservableProperty] private string? _hint;
}

public sealed partial class OptimizeViewModel : ObservableObject
{
    private readonly IOptimizer _optimizer;
    private readonly OperationGate _gate;

    public OptimizeViewModel(IOptimizer optimizer, OperationGate? gate = null)
    {
        _optimizer = optimizer;
        _gate = gate ?? new OperationGate();
        foreach (var t in OptimizeCatalog.All) Items.Add(new OptimizeItemViewModel(t));
    }

    public ObservableCollection<OptimizeItemViewModel> Items { get; } = [];

    [ObservableProperty] private string? _message;

    /// <summary>Reads what the Recycle Bin holds right now and shows it on that task.</summary>
    public async Task RefreshAsync()
    {
        var bytes = await Task.Run(() => _optimizer.RecycleBinBytes());
        var rb = Items.FirstOrDefault(i => i.Task.Id == OptimizeCatalog.Recycle);
        if (rb is null) return;
        rb.Hint = bytes is null ? null : bytes == 0 ? Loc.T("回收站现在是空的") : Loc.F("现在占用 {0}",ByteSize.Format(bytes.Value));
    }

    public async Task RunAsync(OptimizeItemViewModel item)
    {
        if (item.State == OptimizeState.Running) return;
        using var lease = _gate.TryEnter(item.Task.Title);
        if (lease is null) { Message = OperationGate.BusyMessage(_gate.Current); return; }

        Message = null;
        item.State = OptimizeState.Running;
        item.Result = null;
        try
        {
            var r = await _optimizer.RunAsync(item.Task.Id);
            item.State = r.Ok ? OptimizeState.Done : OptimizeState.Failed;
            item.Result = r.Message;
        }
        catch (Exception e)
        {
            item.State = OptimizeState.Failed;
            item.Result = e.Message;
        }

        if (item.Task.Id == OptimizeCatalog.Recycle) await RefreshAsync();
    }
}
