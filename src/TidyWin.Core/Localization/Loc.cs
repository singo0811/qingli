using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace TidyWin.Core.Localization;

/// <summary>
/// Turns the Chinese text the program is written in into the person's language. The Chinese sentence itself is the lookup key,
/// so a sentence without a translation simply stays Chinese (never blank, never a code), and with Chinese chosen nothing is
/// loaded at all. The language is picked once at start-up; changing it takes effect the next time the program starts.
/// </summary>
public sealed class Localizer
{
    private readonly IReadOnlyDictionary<string, string>? _table;

    public Localizer(string language, IReadOnlyDictionary<string, string>? table)
    {
        Language = language;
        _table = language == "en" ? table : null;
    }

    public string Language { get; }

    public string T(string zh) => _table is not null && _table.TryGetValue(zh, out var local) ? local : zh;

    /// <summary>A sentence with {0}-style holes. The holes keep their numbers in every language, so word order may change.</summary>
    public string F(string zhFormat, params object?[] args) => string.Format(T(zhFormat), args);

    /// <summary>"zh", "en" or "auto" (follow Windows' display language: Chinese stays Chinese, everything else is English).</summary>
    public static string Resolve(string? choice, CultureInfo? ui = null) =>
        ResolveFromLanguage(choice, (ui ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName);

    /// <summary>Same, for when the display language is known by its two-letter code ("zh", "de"…).</summary>
    public static string ResolveFromLanguage(string? choice, string? osLanguage)
    {
        if (choice is "zh" or "en") return choice;
        return string.Equals(osLanguage, "zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
    }

    public static IReadOnlyDictionary<string, string> Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.GetString() is { Length: > 0 } v) map[p.Name] = v;
        return map;
    }
}

/// <summary>The program-wide language. <c>Loc.T("中文")</c> and <c>Loc.F("中文 {0}", x)</c> are used wherever text reaches the person.</summary>
public static class Loc
{
    private static volatile Localizer _current = new("zh", null);

    public static string Language => _current.Language;
    public static bool IsEnglish => _current.Language == "en";

    /// <summary>Picks the language for the whole run. Call once, before anything shows text.</summary>
    public static void Use(string? choice, string? osLanguage = null)
    {
        var language = osLanguage is null ? Localizer.Resolve(choice) : Localizer.ResolveFromLanguage(choice, osLanguage);
        IReadOnlyDictionary<string, string>? table = null;
        if (language == "en")
        {
            using var s = typeof(Loc).Assembly.GetManifestResourceStream("TidyWin.Core.Localization.en.json");
            if (s is not null) table = Localizer.Parse(s);
        }
        _current = new Localizer(language, table);
    }

    public static string T(string zh) => _current.T(zh);

    public static string F(string zhFormat, params object?[] args) => _current.F(zhFormat, args);
}
