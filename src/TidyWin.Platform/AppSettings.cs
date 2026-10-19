using System.Globalization;
using System.Text.Json;
using TidyWin.Core.Mascot;

namespace TidyWin.Platform;

/// <summary>
/// The few things the app remembers, in one small JSON file under the user's local app data. It is written by hand
/// (no reflection), is limited in size, and a damaged or missing file just means the defaults.
/// </summary>
public sealed class AppSettings
{
    private const long MaxBytes = 16 * 1024;
    private const int MaxSnoozes = 50;

    /// <summary>"auto" or the name of an outfit.</summary>
    public string Outfit { get; set; } = Personas.Auto;

    /// <summary>"auto" (follow Windows), "zh" or "en". Read once at start; a change shows the next time the program starts.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Where to look for new versions (an https address of a small JSON file). Empty: no update source, nothing is ever contacted.</summary>
    public string UpdateFeed { get; set; } = "";

    /// <summary>Look once a day, at start-up, when a feed is set. It only looks; installing always needs a click.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Last clean-up that really deleted something, so a later suggestion can refer to it.</summary>
    public DateTime? LastCleanUtc { get; set; }
    public long LastCleanBytes { get; set; }

    /// <summary>Free space of the system drive at the previous visit, to notice when it shrinks unusually fast.</summary>
    public DateTime? LastVisitUtc { get; set; }
    public long? DiskFreeAtLastVisit { get; set; }

    /// <summary>Suggestions the person said "not now" to, and until when.</summary>
    public Dictionary<string, DateTime> SnoozedUntil { get; } = new(StringComparer.Ordinal);

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWin", "settings.json");

    public ISet<string> ActiveSnoozes(DateTime nowUtc) =>
        SnoozedUntil.Where(kv => kv.Value > nowUtc).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

    public void Snooze(string id, DateTime untilUtc, DateTime nowUtc)
    {
        foreach (var old in SnoozedUntil.Where(kv => kv.Value <= nowUtc).Select(kv => kv.Key).ToList()) SnoozedUntil.Remove(old);
        if (SnoozedUntil.Count >= MaxSnoozes) return;
        SnoozedUntil[id] = untilUtc;
    }

    public void RememberClean(DateTime nowUtc, long bytes)
    {
        LastCleanUtc = nowUtc;
        LastCleanBytes = bytes;
    }

    public static AppSettings Load(string? path = null)
    {
        var settings = new AppSettings();
        try
        {
            path ??= DefaultPath;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return settings;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return settings;

            if (root.TryGetProperty("outfit", out var o) && o.ValueKind == JsonValueKind.String)
                settings.Outfit = Personas.Normalize(o.GetString());
            if (root.TryGetProperty("language", out var lg) && lg.ValueKind == JsonValueKind.String && lg.GetString() is "zh" or "en" or "auto")
                settings.Language = lg.GetString()!;
            if (root.TryGetProperty("updateFeed", out var uf) && uf.ValueKind == JsonValueKind.String
                && uf.GetString() is { Length: <= 512 } feed && TidyWin.Core.Update.UpdateManifest.TryHttps(feed, out _))
                settings.UpdateFeed = feed;
            if (root.TryGetProperty("autoCheckUpdates", out var ac) && ac.ValueKind is JsonValueKind.True or JsonValueKind.False)
                settings.AutoCheckUpdates = ac.GetBoolean();
            settings.LastUpdateCheckUtc = Date(root, "lastUpdateCheckUtc");
            settings.LastCleanUtc = Date(root, "lastCleanUtc");
            if (root.TryGetProperty("lastCleanBytes", out var lb) && lb.ValueKind == JsonValueKind.Number && lb.TryGetInt64(out var cleaned) && cleaned >= 0)
                settings.LastCleanBytes = cleaned;
            settings.LastVisitUtc = Date(root, "lastVisitUtc");
            if (root.TryGetProperty("diskFreeAtLastVisit", out var df) && df.ValueKind == JsonValueKind.Number && df.TryGetInt64(out var free) && free >= 0)
                settings.DiskFreeAtLastVisit = free;

            if (root.TryGetProperty("snoozed", out var sn) && sn.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in sn.EnumerateObject())
                {
                    if (settings.SnoozedUntil.Count >= MaxSnoozes) break;
                    if (p.Value.ValueKind == JsonValueKind.String && TryDate(p.Value.GetString(), out var until))
                        settings.SnoozedUntil[p.Name] = until;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException
                                      or FormatException or InvalidOperationException)
        {
            // Unreadable or damaged: start again from the defaults. A bad settings file must never stop the app.
            return new AppSettings();
        }
        return settings;
    }

    private static DateTime? Date(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && TryDate(v.GetString(), out var d) ? d : null;

    // Values are written as "O" (round-trip) strings, so they carry their own kind; whatever was read is turned into UTC.
    // (RoundtripKind cannot be combined with AdjustToUniversal: that combination throws.)
    private static bool TryDate(string? s, out DateTime d)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            d = parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
            return true;
        }
        d = default;
        return false;
    }

    /// <summary>Returns false instead of throwing: not being able to remember a choice must never break the app.</summary>
    public bool Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("outfit", Personas.Normalize(Outfit));
                if (Language != "auto") w.WriteString("language", Language);
                if (UpdateFeed.Length > 0) w.WriteString("updateFeed", UpdateFeed);
                if (!AutoCheckUpdates) w.WriteBoolean("autoCheckUpdates", false);
                if (LastUpdateCheckUtc is { } lu) w.WriteString("lastUpdateCheckUtc", lu.ToString("O", CultureInfo.InvariantCulture));
                if (LastCleanUtc is { } lc) w.WriteString("lastCleanUtc", lc.ToString("O", CultureInfo.InvariantCulture));
                if (LastCleanBytes > 0) w.WriteNumber("lastCleanBytes", LastCleanBytes);
                if (LastVisitUtc is { } lv) w.WriteString("lastVisitUtc", lv.ToString("O", CultureInfo.InvariantCulture));
                if (DiskFreeAtLastVisit is { } df) w.WriteNumber("diskFreeAtLastVisit", df);
                if (SnoozedUntil.Count > 0)
                {
                    w.WriteStartObject("snoozed");
                    foreach (var kv in SnoozedUntil) w.WriteString(kv.Key, kv.Value.ToString("O", CultureInfo.InvariantCulture));
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }

            // Write beside it, then swap, so a crash in the middle cannot leave half a file.
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, ms.ToArray());
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
