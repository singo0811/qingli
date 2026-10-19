namespace TidyWin.Core.Paths;

/// <summary>Splits a command as stored in the registry ("C:\Program Files\X\x.exe" /silent) into program and arguments.</summary>
public static class CommandLine
{
    private static readonly string[] ProgramExtensions = [".exe", ".bat", ".cmd", ".com", ".lnk", ".msc", ".cpl"];

    public static (string Exe, string Args) Split(string? command, Func<string, bool>? fileExists = null, Func<string, string>? expand = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return ("", "");
        var s = (expand ?? Environment.ExpandEnvironmentVariables)(command.Trim());

        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end < 0 ? (s[1..], "") : (s[1..end], s[(end + 1)..].Trim());
        }

        // Unquoted: the program is the shortest prefix that ends in a program extension or is a real file,
        // so paths with spaces ("C:\Program Files\...") still work.
        for (var i = 1; i <= s.Length; i++)
        {
            if (i < s.Length && s[i] != ' ') continue;
            var candidate = s[..i];
            if (ProgramExtensions.Any(e => candidate.EndsWith(e, StringComparison.OrdinalIgnoreCase)) || (fileExists?.Invoke(candidate) ?? false))
                return (candidate, s[i..].Trim());
        }

        var first = s.IndexOf(' ');
        return first < 0 ? (s, "") : (s[..first], s[(first + 1)..].Trim());
    }
}
