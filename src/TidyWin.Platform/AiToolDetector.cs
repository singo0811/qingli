using System.Diagnostics;
using TidyWin.Core.Mascot;

namespace TidyWin.Platform;

/// <summary>
/// Notices which AI tools this person uses, for dressing the mascot. It looks at program names that are running and at
/// when a few well-known folders were last changed. It reads no file contents and sends nothing anywhere.
/// </summary>
/// <remarks>
/// The program names and folders come from each tool's own documentation (checked online), not from guesses.
/// Two things need care: OpenAI's Codex desktop app also runs as ChatGPT.exe, so for that name the file's folder is
/// checked too; and Microsoft Store apps keep their data under a folder with a random suffix, so folders may end in *.
/// </remarks>
public sealed class AiToolDetector
{
    /// <param name="NotInPath">A running program only counts if its file is not under a folder containing one of these.</param>
    private sealed record Probe(AiTool Tool, string[] Processes, string[] Folders, string[]? NotInPath = null);

    private static readonly Probe[] Probes =
    [
        new(AiTool.Ollama, ["ollama", "ollama app", "ollama_llama_server"],
            [@"%USERPROFILE%\.ollama", @"%LOCALAPPDATA%\Programs\Ollama"]),

        // %USERPROFILE%\.lmstudio holds the models and settings; %APPDATA%\LM Studio the app state.
        new(AiTool.LmStudio, ["lm studio", "lms"],
            [@"%USERPROFILE%\.lmstudio", @"%APPDATA%\LM Studio", @"%LOCALAPPDATA%\Programs\LM Studio", @"%USERPROFILE%\.cache\lm-studio"]),

        // ComfyUI Desktop: files in Programs\ComfyUI, settings in %APPDATA%\ComfyUI (older) or "Comfy Desktop" (newer).
        new(AiTool.ComfyUi, ["comfyui", "comfy desktop"],
            [@"%LOCALAPPDATA%\Programs\ComfyUI", @"%APPDATA%\ComfyUI", @"%APPDATA%\Comfy Desktop", @"%LOCALAPPDATA%\Comfy-Desktop",
             @"%LOCALAPPDATA%\Programs\@comfyorgcomfyui-electron"]),

        new(AiTool.Cursor, ["cursor"],
            [@"%APPDATA%\Cursor", @"%LOCALAPPDATA%\Programs\cursor"]),

        new(AiTool.HuggingFace, [],
            [@"%USERPROFILE%\.cache\huggingface"]),

        // Claude desktop (installer or Store) and Claude Code, which keeps its settings in %USERPROFILE%\.claude.
        new(AiTool.Claude, ["claude"],
            [@"%APPDATA%\Claude", @"%LOCALAPPDATA%\AnthropicClaude", @"%LOCALAPPDATA%\Packages\Claude_*", @"%USERPROFILE%\.claude"]),

        // The ChatGPT desktop app. OpenAI's Codex app is also called ChatGPT.exe and lives in OpenAI.Codex_*, so it is
        // left out; %LOCALAPPDATA%\OpenAI belongs to Codex too and is not used.
        new(AiTool.ChatGpt, ["chatgpt"],
            [@"%LOCALAPPDATA%\Packages\OpenAI.ChatGPT-Desktop_*", @"%APPDATA%\ChatGPT"],
            NotInPath: ["OpenAI.Codex"]),
    ];

    private readonly Func<IReadOnlyCollection<string>> _processNames;
    private readonly Func<string, IReadOnlyList<string>> _imagePaths;
    private readonly Func<string, DateTime?> _folderLastChanged;
    private readonly Func<string, string> _expand;

    public AiToolDetector() : this(RunningProcessNames, ImagePathsOf, LastChanged, Environment.ExpandEnvironmentVariables) { }

    public AiToolDetector(
        Func<IReadOnlyCollection<string>> processNames,
        Func<string, DateTime?> folderLastChanged,
        Func<string, string> expand)
        : this(processNames, _ => [], folderLastChanged, expand) { }

    public AiToolDetector(
        Func<IReadOnlyCollection<string>> processNames,
        Func<string, IReadOnlyList<string>> imagePaths,
        Func<string, DateTime?> folderLastChanged,
        Func<string, string> expand)
    {
        _processNames = processNames;
        _imagePaths = imagePaths;
        _folderLastChanged = folderLastChanged;
        _expand = expand;
    }

    public IReadOnlyList<ToolSignal> Detect()
    {
        var running = new HashSet<string>(_processNames(), StringComparer.OrdinalIgnoreCase);
        var list = new List<ToolSignal>(Probes.Length);

        foreach (var p in Probes)
        {
            var isRunning = p.Processes.Any(name => running.Contains(name) && CountsAsThisTool(p, name));

            DateTime? last = null;
            foreach (var folder in p.Folders)
            {
                var path = _expand(folder);
                if (path.Contains('%')) continue; // a variable that does not exist here: nothing to look at
                if (_folderLastChanged(path) is { } t && (last is null || t > last)) last = t;
            }
            list.Add(new ToolSignal(p.Tool, isRunning, last));
        }
        return list;
    }

    /// <summary>
    /// Most programs are recognised by name alone. When another product shares the name, at least one running copy must
    /// have a readable file path that is not in the excluded place; an unreadable path is not trusted.
    /// </summary>
    private bool CountsAsThisTool(Probe probe, string name)
    {
        if (probe.NotInPath is not { Length: > 0 } excluded) return true;

        return _imagePaths(name).Any(path =>
            !string.IsNullOrEmpty(path) &&
            !excluded.Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase)));
    }

    // ───────── real system ─────────

    private static IReadOnlyCollection<string> RunningProcessNames()
    {
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { names.Add(p.ProcessName); }
                catch { /* the process ended while we looked */ }
            }
        }
        return names;
    }

    /// <summary>The file path of every running program with this name. Only asked for names that need the check.</summary>
    private static IReadOnlyList<string> ImagePathsOf(string name)
    {
        var paths = new List<string>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try { if (p.MainModule?.FileName is { } f) paths.Add(f); }
                catch { /* no permission, or it just ended: counts as unknown */ }
            }
        }
        return paths;
    }

    /// <summary>
    /// When the folder was last changed, or null if it is missing. A name ending in * (Microsoft Store apps add a random
    /// suffix) matches every folder that starts that way, and the newest one counts.
    /// </summary>
    public static DateTime? LastChanged(string path)
    {
        try
        {
            if (!path.Contains('*')) return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : null;

            var parent = Path.GetDirectoryName(path);
            var pattern = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || parent.Contains('*') || !Directory.Exists(parent)) return null;

            DateTime? newest = null;
            foreach (var dir in Directory.EnumerateDirectories(parent, pattern))
            {
                var t = Directory.GetLastWriteTimeUtc(dir);
                if (newest is null || t > newest) newest = t;
            }
            return newest;
        }
        catch { return null; }
    }
}
