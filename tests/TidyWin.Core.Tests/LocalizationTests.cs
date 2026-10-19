using System.Globalization;
using System.Text.RegularExpressions;
using TidyWin.Core.Localization;
using Xunit;

namespace TidyWin.Core.Tests;

public class LocalizationTests
{
    private static readonly Regex Hole = new(@"\{\d+(:[^}]*)?\}", RegexOptions.Compiled);
    private static readonly Regex Cjk = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    private static IReadOnlyDictionary<string, string> Table()
    {
        using var s = typeof(Loc).Assembly.GetManifestResourceStream("TidyWin.Core.Localization.en.json");
        Assert.NotNull(s);
        return Localizer.Parse(s);
    }

    [Fact]
    public void The_english_table_is_embedded_and_not_empty()
        => Assert.True(Table().Count > 1000);

    [Fact]
    public void Every_translation_keeps_the_same_holes_as_its_sentence()
    {
        foreach (var (zh, en) in Table())
        {
            var a = Hole.Matches(zh).Select(m => m.Value).OrderBy(x => x).ToArray();
            var b = Hole.Matches(en).Select(m => m.Value).OrderBy(x => x).ToArray();
            Assert.True(a.SequenceEqual(b), $"holes differ: {zh} => {en}");
        }
    }

    [Fact]
    public void No_translation_still_contains_chinese()
    {
        foreach (var (zh, en) in Table())
            Assert.False(Cjk.IsMatch(en), $"still Chinese: {zh} => {en}");
    }

    [Fact]
    public void Every_translation_formats_without_throwing()
    {
        foreach (var (zh, en) in Table())
        {
            var args = Enumerable.Range(0, 6).Select(i => (object?)i).ToArray();
            var ex = Record.Exception(() => string.Format(CultureInfo.InvariantCulture, en, args));
            Assert.True(ex is null, $"bad format: {zh} => {en}");
        }
    }

    [Fact]
    public void English_looks_up_by_the_chinese_sentence_and_falls_back_to_it()
    {
        var l = new Localizer("en", new Dictionary<string, string> { ["你好"] = "Hello", ["清了 {0} 个"] = "Cleared {0}" });
        Assert.Equal("Hello", l.T("你好"));
        Assert.Equal("Cleared 3", l.F("清了 {0} 个", 3));
        Assert.Equal("没翻译", l.T("没翻译"));
    }

    [Fact]
    public void Chinese_never_touches_the_table()
    {
        var l = new Localizer("zh", new Dictionary<string, string> { ["你好"] = "Hello" });
        Assert.Equal("你好", l.T("你好"));
    }

    [Theory]
    [InlineData("zh", "en", "zh")]
    [InlineData("en", "zh-CN", "en")]
    [InlineData("auto", "zh-CN", "zh")]
    [InlineData("auto", "zh-TW", "zh")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "de-DE", "en")]
    [InlineData(null, "ja-JP", "en")]
    [InlineData("klingon", "zh-CN", "zh")]
    public void The_language_is_chosen_from_the_setting_then_the_windows_language(string? choice, string ui, string expected)
        => Assert.Equal(expected, Localizer.Resolve(choice, new CultureInfo(ui)));
}
