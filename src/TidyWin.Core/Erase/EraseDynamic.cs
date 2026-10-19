using System.Text.Json;
using System.Text.RegularExpressions;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Paths;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Erase;

/// <summary>
/// Places only the disk can tell: the project folders a tool remembers (so the <c>.claude</c> or <c>.cursor</c> folder inside each
/// can be offered), and the folders ComfyUI was pointed at. Only the names of the remembered folders are read; the rest of a
/// file (which may hold an account address) is never kept.
/// </summary>
internal static class EraseDynamic
{
    private const int MaxProjects = 300;
    private const long MaxConfigBytes = 64L * 1024 * 1024;

    // ───────────────────────── projects ─────────────────────────

    /// <summary>Offers <paramref name="places"/> (a folder or a file name) inside every project folder the tool remembers.</summary>
    public static Func<IAiEnvironment, IFileSystem, IReadOnlyList<ResolvedErase>> Projects(
        string toolId, params (string Name, bool IsFile)[] places) =>
        (env, fs) =>
        {
            var result = new List<ResolvedErase>();
            var n = 0;
            foreach (var project in FindProjects(toolId, env, fs))
            {
                n++;
                var name = System.IO.Path.GetFileName(project.TrimEnd('\\'));
                foreach (var (place, isFile) in places)
                {
                    var path = WindowsPath.Normalize(project + "\\" + place);
                    if (path is null) continue;
                    var loc = new EraseLocation(
                        $"{toolId}.project.{n}.{place}", EraseKind.Settings,
                        Loc.F("项目「{0}」里的 {1}",name,place),
                        Loc.T("放在项目文件夹里,通常是项目自己的一部分(可能已经提交到 git)。默认不勾。"),
                        EraseRoot.Absolute, path, isFile);
                    result.Add(new ResolvedErase(loc, path));
                }
            }
            return result;
        };

    public static IReadOnlyList<string> FindProjects(string toolId, IAiEnvironment env, IFileSystem fs)
    {
        var raw = new List<string>();
        var profile = env.Folder(KnownFolder.UserProfile);
        var appData = env.Folder(KnownFolder.AppData);

        switch (toolId)
        {
            case "claude":
                ClaudeProjects(env, fs, profile, raw);
                break;
            case "cursor":
                WorkspaceFolders(fs, Join(appData, @"Cursor\User\workspaceStorage"), raw);
                break;
            case "windsurf":
                WorkspaceFolders(fs, Join(appData, @"Windsurf\User\workspaceStorage"), raw);
                break;
            case "codex":
                CodexProjects(env, fs, profile, raw);
                break;
            case "gemini":
                GeminiProjects(fs, profile, raw);
                break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var r in raw)
        {
            var p = WindowsPath.Normalize(r);
            if (p is null || !AiErase.IsSafeHomeOverride(p, env)) continue; // a drive root, the profile itself, or a system folder is never a project
            if (!fs.DirectoryExists(p) || !seen.Add(p)) continue;
            list.Add(p);
            if (list.Count >= MaxProjects) break;
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static string Join(string? a, string b) => string.IsNullOrWhiteSpace(a) ? "" : a.TrimEnd('\\', '/') + "\\" + b;

    private static void ClaudeProjects(IAiEnvironment env, IFileSystem fs, string profile, List<string> raw)
    {
        var files = new List<string> { Join(profile, ".claude.json") };
        if (env.Variable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom && AiErase.IsSafeHomeOverride(custom, env))
            files.Add(Join(custom, ".claude.json"));

        foreach (var file in files.Where(f => f.Length > 0))
        {
            if (fs.ReadText(file, MaxConfigBytes) is not { } text) continue;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("projects", out var projects)
                    && projects.ValueKind == JsonValueKind.Object)
                    foreach (var p in projects.EnumerateObject()) raw.Add(p.Name);
            }
            catch (JsonException) { }
        }
    }

    /// <summary>VS Code style editors keep one folder per workspace, each with a workspace.json naming the folder it was opened on.</summary>
    private static void WorkspaceFolders(IFileSystem fs, string storage, List<string> raw)
    {
        if (storage.Length == 0) return;
        foreach (var dir in fs.EnumerateDirectories(storage))
        {
            if (fs.ReadText(dir + @"\workspace.json", 64 * 1024) is not { } text) continue;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("folder", out var f)
                    && f.ValueKind == JsonValueKind.String
                    && FromFileUri(f.GetString()) is { } path)
                    raw.Add(path);
            }
            catch (JsonException) { }
        }
    }

    private static string? FromFileUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null; // remote and virtual workspaces are not on this disk
        var rest = Uri.UnescapeDataString(uri[5..].TrimStart('/'));      // "c%3A/Users/x" -> "c:/Users/x"
        return Regex.IsMatch(rest, @"^[A-Za-z]:[/\\]") ? rest.Replace('/', '\\') : null;   // a network share or anything else is not a local folder
    }

    private static readonly Regex CodexProject = new(
        @"^\s*\[projects\.(?:""(?<d>(?:[^""\\]|\\.)*)""|'(?<s>[^']*)')\]", RegexOptions.Multiline | RegexOptions.Compiled);

    private static void CodexProjects(IAiEnvironment env, IFileSystem fs, string profile, List<string> raw)
    {
        var home = env.Variable("CODEX_HOME") is { Length: > 0 } c && AiErase.IsSafeHomeOverride(c, env) ? c : Join(profile, ".codex");
        if (home.Length == 0 || fs.ReadText(home.TrimEnd('\\') + @"\config.toml", 4 * 1024 * 1024) is not { } text) return;
        foreach (Match m in CodexProject.Matches(text))
        {
            if (m.Groups["s"].Success) raw.Add(m.Groups["s"].Value);
            else if (m.Groups["d"].Success) raw.Add(m.Groups["d"].Value.Replace(@"\\", @"\"));
        }
    }

    private static void GeminiProjects(IFileSystem fs, string profile, List<string> raw)
    {
        var file = Join(profile, @".gemini\trustedFolders.json");
        if (file.Length == 0 || fs.ReadText(file, 4 * 1024 * 1024) is not { } text) return;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject()) raw.Add(p.Name);
        }
        catch (JsonException) { }
    }

    // ───────────────────────── ComfyUI ─────────────────────────

    /// <summary>The data folders ComfyUI Desktop was pointed at (its own settings say where), beyond the default one in Documents.</summary>
    public static IReadOnlyList<ResolvedErase> ComfyBases(IAiEnvironment env, IFileSystem fs, Func<string, EraseRoot, string, IEnumerable<EraseLocation>> places)
    {
        var candidates = new List<string>();
        foreach (var folder in new[] { "ComfyUI", "Comfy Desktop" })
        {
            var dir = Join(env.Folder(KnownFolder.AppData), folder);
            if (dir.Length == 0) continue;

            if (fs.ReadText(dir + @"\config.json", 1024 * 1024) is { } json)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var p in doc.RootElement.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.String
                                && (p.Name.Equals("basePath", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("base_path", StringComparison.OrdinalIgnoreCase)))
                                candidates.Add(p.Value.GetString()!);
                }
                catch (JsonException) { }
            }

            if (fs.ReadText(dir + @"\extra_models_config.yaml", 1024 * 1024) is { } yaml)
                foreach (Match m in Regex.Matches(yaml, @"^\s*base_path:\s*(?<v>.+?)\s*$", RegexOptions.Multiline))
                    candidates.Add(m.Groups["v"].Value.Trim('"', '\''));
        }

        var result = new List<ResolvedErase>();
        var n = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            var p = WindowsPath.Normalize(c);
            if (p is null || !AiErase.IsSafeHomeOverride(p, env) || !fs.DirectoryExists(p) || !seen.Add(p)) continue;
            n++;
            foreach (var loc in places($"comfyui.base{n}.", EraseRoot.Absolute, p))
                if (WindowsPath.Normalize(loc.RelativePath) is { } path) result.Add(new ResolvedErase(loc, path));
        }
        return result;
    }
}
