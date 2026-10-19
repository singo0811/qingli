using System.Text.Json;
using TidyWin.Core.Localization;

namespace TidyWin.Core.AiJunk;

public sealed record RuleResult(IReadOnlyList<AiTarget> Targets, IReadOnlyList<string> Problems);

/// <summary>
/// Lets a person add their own clean-up locations in a small JSON file. A rule can never be more dangerous than the
/// built-in ones: it must be a cache-like place (auto-rebuild or re-download), deep inside a known folder, never a
/// personal folder, and every rule is still checked by the same path guard right before anything is deleted.
/// </summary>
/// <remarks>
/// Rules are not cryptographically signed. They are read from the person's own profile and treated as the person's
/// own choice, which is why the strict limits above apply regardless of who wrote the file.
/// </remarks>
public static class RuleFile
{
    public const int MaxRules = 100;

    private static readonly string[] ForbiddenFirstSegments =
    [
        "Documents", "Desktop", "Pictures", "Videos", "Music", "Downloads", "OneDrive", ".ssh", ".gnupg", "Microsoft",
        "Contacts", "Favorites", "Saved Games", "Packages", "Programs", "Temp",
    ];

    public static RuleResult Parse(string? json)
    {
        var targets = new List<AiTarget>();
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return new(targets, problems);

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("targets", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new(targets, [Loc.T("缺少 targets 数组")]);

            var index = 0;
            foreach (var el in arr.EnumerateArray())
            {
                index++;
                if (index > MaxRules) { problems.Add(Loc.F("规则太多,只读取前 {0} 条",MaxRules)); break; }
                var (target, error) = ParseOne(el);
                if (target is not null) targets.Add(target); else problems.Add(Loc.F("第 {0} 条:{1}",index,error));
            }
        }
        catch (JsonException e)
        {
            problems.Add(Loc.T("不是有效的 JSON:") + e.Message);
        }
        return new(targets, problems);
    }

    private static (AiTarget?, string) ParseOne(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return (null, Loc.T("不是对象"));

        string? Get(string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var id = Get("id");
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("user.", StringComparison.Ordinal) || id.Length > 64)
            return (null, Loc.T("id 必须以 user. 开头"));

        var title = Get("title");
        var consequence = Get("consequence");
        if (string.IsNullOrWhiteSpace(title)) return (null, Loc.T("缺少 title"));
        if (string.IsNullOrWhiteSpace(consequence)) return (null, Loc.T("缺少 consequence:要写清删掉之后会怎样"));

        KnownFolder anchor;
        switch (Get("anchor")?.ToLowerInvariant())
        {
            case "localappdata": anchor = KnownFolder.LocalAppData; break;
            case "appdata": anchor = KnownFolder.AppData; break;
            case "userprofile": anchor = KnownFolder.UserProfile; break;
            default: return (null, Loc.T("anchor 只能是 LocalAppData、AppData 或 UserProfile"));
        }

        var safety = Get("safety")?.ToLowerInvariant() switch
        {
            "regenerable" => AiSafety.Regenerable,
            "redownloadable" => AiSafety.ReDownloadable,
            _ => (AiSafety?)null,
        };
        if (safety is null) return (null, Loc.T("safety 只能是 regenerable 或 redownloadable"));

        var path = Get("path")?.Replace('/', '\\').Trim('\\', ' ');
        if (string.IsNullOrEmpty(path)) return (null, Loc.T("缺少 path"));
        if (path.Contains('*') || path.Contains('?')) return (null, Loc.T("path 不能含通配符"));
        if (Path.IsPathRooted(path) || path.Contains(':')) return (null, Loc.T("path 必须是相对路径"));

        var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or "..")) return (null, Loc.T("path 不能含 . 或 .."));
        if (segments.Length < 2) return (null, Loc.T("path 至少要两级,不能指向 ") + anchor + Loc.T(" 下的某个顶层文件夹"));
        if (ForbiddenFirstSegments.Contains(segments[0], StringComparer.OrdinalIgnoreCase)) return (null, Loc.F("不允许指向「{0}」",segments[0]));
        if (anchor == KnownFolder.UserProfile && segments[0].Equals("AppData", StringComparison.OrdinalIgnoreCase) && segments.Length < 3)
            return (null, Loc.T("路径太浅"));

        int? minAge = null;
        if (el.TryGetProperty("minAgeDays", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var days) && days is >= 0 and <= 3650)
            minAge = days;

        return (new AiTarget(id, Get("tool") ?? Loc.T("自定义"), title.Trim(), AiCategory.AppCache, safety.Value, anchor,
            string.Join('\\', segments), [], consequence.Trim(), minAge), "");
    }
}
