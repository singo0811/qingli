using TidyWin.Core.Localization;
namespace TidyWin.Core.Safety;

/// <summary>
/// Only one thing that changes the computer may run at a time: a clean-up, a model removal, an uninstall, an
/// optimization. Every page asks this gate first, so two buttons pressed in two pages can never fight over the same files.
/// </summary>
public sealed class OperationGate
{
    public static OperationGate Shared { get; } = new();

    private readonly object _lock = new();
    private string? _current;

    /// <summary>What is running now ("清理系统垃圾"), or null.</summary>
    public string? Current { get { lock (_lock) return _current; } }

    /// <summary>Returns a lease to dispose when finished, or null when something else is already running.</summary>
    public IDisposable? TryEnter(string name)
    {
        lock (_lock)
        {
            if (_current is not null) return null;
            _current = name;
        }
        return new Lease(this);
    }

    public static string BusyMessage(string? running) => Loc.F("正在{0},等它结束再试。",running ?? Loc.T("处理别的事"));

    private void Leave() { lock (_lock) _current = null; }

    private sealed class Lease(OperationGate gate) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.Leave();
        }
    }
}
