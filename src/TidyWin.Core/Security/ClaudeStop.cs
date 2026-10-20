namespace TidyWin.Core.Security;

/// <summary>The desktop app and the command-line tool are both named Claude. Nothing else is.</summary>
public static class ClaudeStop
{
    public static bool IsClaude(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Equals("claude", StringComparison.OrdinalIgnoreCase);
    }
}
