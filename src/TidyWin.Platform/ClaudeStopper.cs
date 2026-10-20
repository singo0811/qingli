using System.Diagnostics;
using TidyWin.Core.Security;

namespace TidyWin.Platform;

/// <summary>
/// Closes Claude. A window is asked to exit first, so it can still offer to save. A process with no window,
/// or one that does not leave, is then ended. Only processes named Claude are touched.
/// </summary>
public static class ClaudeStopper
{
    public static int Close()
    {
        var closed = 0;
        Process[] found;
        try { found = Process.GetProcessesByName("claude"); }
        catch (Exception) { return 0; }

        foreach (var process in found)
        {
            using (process)
            {
                try
                {
                    if (!ClaudeStop.IsClaude(process.ProcessName)) continue;
                    var asked = false;
                    try { asked = process.CloseMainWindow(); } catch (InvalidOperationException) { }
                    if (asked && process.WaitForExit(2500)) { closed++; continue; }
                    process.Kill(entireProcessTree: true);
                    closed++;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Already gone, or this user cannot end it.
                }
            }
        }
        return closed;
    }
}
