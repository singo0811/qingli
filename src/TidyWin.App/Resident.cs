using TidyWin.Core.Localization;
using TidyWin.Platform;
using TidyWin.ViewModels;

namespace TidyWin.App;

/// <summary>
/// While the program is running, including when the window is hidden: return idle memory, and clear caches that rebuild themselves.
/// </summary>
public sealed class Resident : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly IdleMemory _memory = new();
    private readonly CancellationTokenSource _stop = new();

    public Resident(TrayIcon tray) => _tray = tray;

    public void Start()
    {
        _ = MemoryLoop(_stop.Token);
        _ = JunkLoop(_stop.Token);
    }

    public void Dispose() => _stop.Cancel();

    private static bool On => AppSettings.Load().AutoMaintain;

    private async Task MemoryLoop(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            while (!ct.IsCancellationRequested)
            {
                if (On)
                {
                    var trimmed = 0;
                    try { trimmed = await Task.Run(() => _memory.TrimOnce(), ct); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    if (trimmed > 0)
                        _tray.Balloon(Loc.T("空闲内存已让出"), Loc.F("整理了 {0} 个空闲程序占用的内存。程序都还在,用到时会自己拿回去。", trimmed));
                }
                await Task.Delay(AutoMaintain.MemoryInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task JunkLoop(CancellationToken ct)
    {
        try
        {
            var last = AppSettings.Load().LastAutoCleanUtc;
            var since = last is { } at ? DateTime.UtcNow - at : TimeSpan.MaxValue;
            var first = since >= AutoMaintain.JunkInterval ? TimeSpan.FromMinutes(3) : AutoMaintain.JunkInterval - since;
            await Task.Delay(first, ct);
            while (!ct.IsCancellationRequested)
            {
                if (On)
                {
                    long? freed = null;
                    try { freed = await Task.Run(() => AutoMaintain.CleanRegenerable(ct), ct); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { }
                    if (freed is { } bytes)
                    {
                        var settings = AppSettings.Load();
                        settings.LastAutoCleanUtc = DateTime.UtcNow;
                        settings.Save();
                        if (bytes > 0)
                            _tray.Balloon(Loc.T("缓存已清理"), Loc.F("清掉了会自动重建的缓存,释放 {0}。聊天记录、模型和你的文件没有动。", ByteSize.Format(bytes)));
                    }
                }
                await Task.Delay(AutoMaintain.JunkInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
