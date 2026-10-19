using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TidyWin.Core.Memory;
using TidyWin.Platform;
using TidyWin.ViewModels;

namespace TidyWin.App;

/// <summary>
/// A page only does work while somebody can see it. <see cref="Activate"/> is called each time the page is shown,
/// <see cref="Deactivate"/> when another page takes over or the window goes away.
/// </summary>
public interface IPageLifecycle
{
    void Activate();
    void Deactivate();
}

/// <summary>
/// Reads memory in the background so leak detection has history even when the memory page is not open,
/// but slowly: quick while the page is on screen, rare otherwise.
/// </summary>
public sealed class MemorySampler
{
    public static readonly TimeSpan Watching = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan Background = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Minimized = TimeSpan.FromSeconds(60);

    private readonly DispatcherQueueTimer _timer;
    private bool _refreshing;

    public MemoryViewModel ViewModel { get; }

    public MemorySampler(DispatcherQueue queue)
    {
        ViewModel = new MemoryViewModel(
            new WindowsMemorySource(),
            new WindowsProcessControl(),
            new OllamaClient(),
            new ProtectionPolicy(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.ProcessId),
            Environment.ProcessorCount);

        _timer = queue.CreateTimer();
        _timer.Interval = Background; // the first reading comes after one interval, so start-up stays quiet
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    public void SetCadence(TimeSpan interval)
    {
        if (_timer.Interval != interval) _timer.Interval = interval;
    }

    public async Task TickAsync()
    {
        if (_refreshing || ViewModel.Busy) return;
        _refreshing = true;
        try { await ViewModel.RefreshAsync(); }
        catch { /* a failed reading just skips this round */ }
        finally { _refreshing = false; }
    }
}
