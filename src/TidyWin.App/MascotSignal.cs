using TidyWin.Core.Mascot;

namespace TidyWin.App;

/// <summary>A page that has something to tell the mascot about what it is showing.</summary>
public interface IMascotSource
{
    MascotSignal Mascot { get; }
}

/// <summary>Holds what the page wants the mascot to say, and tells the window when that changes.</summary>
public sealed class MascotSignal
{
    public MascotSignal(MascotState initial) => State = initial;

    public MascotState State { get; private set; }

    public event Action? Changed;

    /// <summary>Setting the same state again does nothing, so pages can call this on every refresh.</summary>
    public void Set(MascotState state)
    {
        if (state == State) return;
        State = state;
        Changed?.Invoke();
    }
}
