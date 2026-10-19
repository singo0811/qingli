using System.Diagnostics;

namespace TidyWin.Platform;

/// <summary>
/// Whether something is running from inside a folder, judged by where the running program's file lives. A program started
/// from somewhere else that merely reads files in the folder is not noticed, so callers should treat "no" as "not seen".
/// </summary>
public static class RunningPrograms
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(2);
    private static readonly object Gate = new();
    private static string[] _images = [];
    private static long _stamp;

    public static bool IsRunningUnder(string folder)
    {
        var prefix = folder.TrimEnd('\\') + "\\";
        foreach (var image in Snapshot())
            if (image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string[] Snapshot()
    {
        lock (Gate)
        {
            if (_stamp != 0 && Stopwatch.GetElapsedTime(_stamp) < Fresh) return _images;

            var list = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (WindowsMemorySource.QueryImagePath(p.Id) is { Length: > 0 } path) list.Add(path);
                }
                finally { p.Dispose(); }
            }
            _images = [.. list];
            _stamp = Stopwatch.GetTimestamp();
            return _images;
        }
    }
}
