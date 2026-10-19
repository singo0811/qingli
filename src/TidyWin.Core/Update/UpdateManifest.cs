using System.Text.Json;
using System.Text.RegularExpressions;

namespace TidyWin.Core.Update;

/// <summary>What the update feed says about the newest version.</summary>
public sealed record UpdateInfo(Version Version, Uri Url, string Sha256, long Size, string Notes);

/// <summary>
/// Reads and judges the small JSON file an update feed serves. Everything is checked here before anything is downloaded:
/// the address must be https, the file must come with a SHA-256 and a size, and the size has a ceiling, so a damaged or
/// hostile feed can neither point at a plain-http file nor make the program fill the disk.
/// <code>{ "version": "1.2.0", "url": "https://…/TidyWin-Setup-1.2.0.exe", "sha256": "64 hex", "size": 31457280,
///   "notes": "text" | { "zh": "…", "en": "…" } }</code>
/// </summary>
public static partial class UpdateManifest
{
    public const int MaxFeedBytes = 32 * 1024;
    public const long MaxInstallerBytes = 400L * 1024 * 1024;
    private const int MaxNotesChars = 2000;

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    /// <summary>Returns null and sets <paramref name="problem"/> when the feed is not acceptable.</summary>
    public static UpdateInfo? Parse(string json, string language, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxFeedBytes) return Fail("empty or too large", out problem);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("not an object", out problem);

            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String || !TryVersion(v.GetString(), out var version))
                return Fail("bad version", out problem);

            if (!root.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String || !TryHttps(u.GetString(), out var url))
                return Fail("the download address must be https", out problem);

            if (!root.TryGetProperty("sha256", out var h) || h.ValueKind != JsonValueKind.String || h.GetString() is not { } hash || !Sha256Pattern().IsMatch(hash))
                return Fail("missing or bad sha256", out problem);

            if (!root.TryGetProperty("size", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out var size) || size < 1 || size > MaxInstallerBytes)
                return Fail("missing or unreasonable size", out problem);

            return new UpdateInfo(version, url, hash.ToLowerInvariant(), size, Notes(root, language));
        }
        catch (JsonException)
        {
            return Fail("not valid JSON", out problem);
        }
    }

    public static bool IsNewer(Version current, Version offered) => Normalize(offered) > Normalize(current);

    /// <summary>"1.2" and "1.2.0.0" are the same version.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public static bool TryVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32) return false;
        var t = text.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(t, out var parsed) || parsed.Major < 0 || parsed.Minor < 0) return false;
        version = parsed;
        return true;
    }

    /// <summary>Only https, with a host, and without credentials in the address.</summary>
    public static bool TryHttps(string? text, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        uri = parsed;
        return true;
    }

    private static string Notes(JsonElement root, string language)
    {
        if (!root.TryGetProperty("notes", out var n)) return "";
        string? text = n.ValueKind switch
        {
            JsonValueKind.String => n.GetString(),
            JsonValueKind.Object when n.TryGetProperty(language, out var l) && l.ValueKind == JsonValueKind.String => l.GetString(),
            JsonValueKind.Object when n.TryGetProperty("en", out var e) && e.ValueKind == JsonValueKind.String => e.GetString(),
            _ => null,
        };
        text = (text ?? "").Trim();
        return text.Length > MaxNotesChars ? text[..MaxNotesChars] + "…" : text;
    }

    private static UpdateInfo? Fail(string why, out string? problem)
    {
        problem = why;
        return null;
    }
}
