using Microsoft.UI.Dispatching;

namespace TidyWin.App;

/// <summary>Eases a number toward a target. The big hero figure uses it so changes roll instead of jump.</summary>
public sealed class Tween
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Action<double> _apply;
    private double _current;
    private double _target;

    public Tween(DispatcherQueue queue, Action<double> apply)
    {
        _apply = apply;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) =>
        {
            _current += (_target - _current) * 0.16;
            if (Math.Abs(_target - _current) < 1) { _current = _target; _timer.Stop(); }
            _apply(_current);
        };
    }

    public void To(double target)
    {
        _target = target;
        if (!_timer.IsRunning) _timer.Start();
    }

    public void Jump(double value)
    {
        _timer.Stop();
        _current = _target = value;
        _apply(value);
    }
}
