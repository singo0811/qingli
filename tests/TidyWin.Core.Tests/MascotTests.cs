using TidyWin.Core.Mascot;
using TidyWin.Core.Memory;
using TidyWin.Core.Status;
using Xunit;

namespace TidyWin.Core.Tests;

public class PersonaPickerTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static ToolSignal Sig(AiTool t, bool running = false, double? daysAgo = null) =>
        new(t, running, daysAgo is null ? null : Now.AddDays(-daysAgo.Value));

    [Fact]
    public void A_tool_that_is_neither_installed_nor_running_scores_nothing() =>
        Assert.Equal(0, Personas.Score(Sig(AiTool.Ollama), Now));

    [Theory]
    [InlineData(0.5, 60)]
    [InlineData(1, 60)]
    [InlineData(3, 40)]
    [InlineData(7, 40)]
    [InlineData(20, 20)]
    [InlineData(30, 20)]
    [InlineData(90, 5)]
    public void Recent_use_scores_by_how_recent(double days, int expected) =>
        Assert.Equal(expected, Personas.Score(Sig(AiTool.Cursor, daysAgo: days), Now));

    [Fact]
    public void Running_counts_more_than_any_amount_of_history()
    {
        var running = Personas.Score(Sig(AiTool.Cursor, running: true), Now);
        var recent = Personas.Score(Sig(AiTool.Ollama, daysAgo: 0.1), Now);
        Assert.True(running > recent);
    }

    [Fact]
    public void The_tool_in_use_now_wins()
    {
        var pick = Personas.Pick([Sig(AiTool.Ollama, daysAgo: 2), Sig(AiTool.Cursor, running: true, daysAgo: 0.1)], Now);
        Assert.Equal(AiTool.Cursor, pick);
    }

    [Fact]
    public void A_tie_goes_to_the_fixed_priority_not_to_list_order()
    {
        // Both running and touched today: Ollama outranks Cursor whichever comes first.
        var a = Personas.Pick([Sig(AiTool.Cursor, true, 0.1), Sig(AiTool.Ollama, true, 0.1)], Now);
        var b = Personas.Pick([Sig(AiTool.Ollama, true, 0.1), Sig(AiTool.Cursor, true, 0.1)], Now);
        Assert.Equal(AiTool.Ollama, a);
        Assert.Equal(AiTool.Ollama, b);
    }

    [Fact]
    public void Nothing_in_use_picks_nothing_and_looks_classic()
    {
        var pick = Personas.Pick([Sig(AiTool.Ollama), Sig(AiTool.Claude)], Now);
        Assert.Null(pick);
        Assert.Equal(Outfit.Classic, Personas.Resolve("auto", pick));
        Assert.Empty(Personas.InUse([Sig(AiTool.Ollama)], Now));
    }

    [Fact]
    public void In_use_lists_every_tool_most_relevant_first()
    {
        var list = Personas.InUse(
            [Sig(AiTool.HuggingFace, daysAgo: 20), Sig(AiTool.Cursor, true, 0.1), Sig(AiTool.Ollama, daysAgo: 3), Sig(AiTool.ChatGpt)], Now);
        Assert.Equal([AiTool.Cursor, AiTool.Ollama, AiTool.HuggingFace], list);
    }

    [Fact]
    public void Every_tool_has_its_own_outfit()
    {
        var all = Enum.GetValues<AiTool>();
        var outfits = all.Select(Personas.For).ToList();
        Assert.Equal(all.Length, outfits.Distinct().Count());
        Assert.DoesNotContain(Outfit.Classic, outfits);
    }

    // Colours come from each tool's published brand palette (checked online). Ollama and OpenAI publish no colour at all.
    [Theory]
    [InlineData(Outfit.Fox, "F2FF59")]       // Comfy Yellow
    [InlineData(Outfit.Hardhat, "F54E00")]   // Cursor Orange
    [InlineData(Outfit.Hugger, "FFD21E")]    // Hugging Yellow
    [InlineData(Outfit.Claude, "D97757")]    // Claude terracotta
    public void Outfit_accents_are_the_tools_published_brand_colours(Outfit outfit, string hex) =>
        Assert.Equal(hex, Personas.Accent(outfit));

    [Fact]
    public void Black_and_white_brands_get_a_neutral_accent_not_an_invented_colour()
    {
        foreach (var o in new[] { Outfit.Llama, Outfit.ChatGpt })
        {
            var hex = Personas.Accent(o);
            var r = Convert.ToInt32(hex[..2], 16); var g = Convert.ToInt32(hex[2..4], 16); var b = Convert.ToInt32(hex[4..], 16);
            Assert.True(Math.Abs(r - g) <= 4 && Math.Abs(g - b) <= 4, $"{o} accent {hex} is not grey");
        }
    }

    [Fact]
    public void Claude_and_ChatGpt_are_separate_tools_with_separate_outfits()
    {
        Assert.NotEqual(Personas.For(AiTool.Claude), Personas.For(AiTool.ChatGpt));
        Assert.Equal(Outfit.Claude, Personas.For(AiTool.Claude));
        Assert.Equal(Outfit.ChatGpt, Personas.For(AiTool.ChatGpt));
    }

    [Fact]
    public void Every_outfit_has_a_name_a_colour_and_a_greeting_either_way()
    {
        foreach (var o in Enum.GetValues<Outfit>())
        {
            Assert.False(string.IsNullOrWhiteSpace(Personas.OutfitName(o)));
            Assert.Matches("^[0-9A-F]{6}$", Personas.Accent(o));
            Assert.False(string.IsNullOrWhiteSpace(Personas.Greeting(o, true)));
            Assert.False(string.IsNullOrWhiteSpace(Personas.Greeting(o, false)));
        }
        foreach (var t in Enum.GetValues<AiTool>()) Assert.False(string.IsNullOrWhiteSpace(Personas.ToolName(t)));
    }

    // ───────── the saved choice ─────────

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("  Llama ", "llama")]
    [InlineData("hardhat", "hardhat")]
    [InlineData("coder", "auto")]      // an outfit name from an older build: back to automatic
    [InlineData("3", "auto")]          // a number is not an outfit
    [InlineData("Llama,Studio", "auto")]
    [InlineData("../../x", "auto")]
    public void Saved_values_are_cleaned_up(string? saved, string expected) =>
        Assert.Equal(expected, Personas.Normalize(saved));

    [Fact]
    public void A_manual_choice_beats_what_was_detected() =>
        Assert.Equal(Outfit.Studio, Personas.Resolve("studio", AiTool.Cursor));

    [Fact]
    public void Auto_follows_the_detected_tool() =>
        Assert.Equal(Outfit.Hardhat, Personas.Resolve("auto", AiTool.Cursor));

    [Fact]
    public void A_garbage_choice_falls_back_to_auto() =>
        Assert.Equal(Outfit.Llama, Personas.Resolve("nonsense", AiTool.Ollama));

    [Fact]
    public void Clicking_walks_through_every_outfit_and_comes_back_to_auto()
    {
        var seen = new List<string>();
        var c = Personas.Auto;
        for (var i = 0; i < Personas.Choices.Count; i++) { c = Personas.NextChoice(c); seen.Add(c); }

        Assert.Equal(Personas.Auto, seen[^1]);
        Assert.Equal(Personas.Choices.Count, seen.Distinct().Count());
        foreach (var o in Enum.GetValues<Outfit>()) Assert.Contains(Personas.ChoiceOf(o), seen);
    }

    [Fact]
    public void Clicking_from_a_damaged_choice_starts_from_auto() =>
        Assert.Equal(Personas.Choices[1], Personas.NextChoice("???"));
}

public class MascotLinesTests
{
    [Fact]
    public void Lines_carry_the_real_numbers_they_are_given()
    {
        Assert.Contains("3.2 GB", MascotLines.AiFound("3.2 GB", 4).Line);
        Assert.Contains("4 项", MascotLines.AiFound("3.2 GB", 4).Line);
        Assert.Contains("1.5 GB", MascotLines.AiDone("1.5 GB", preview: false).Line);
        Assert.Contains("5.0 GB", MascotLines.ModelsSelected(2, "5.0 GB").Line);
        Assert.Contains("12.0 GB", MascotLines.ModelsFound("12.0 GB", 3).Line);
        Assert.Contains("9.1 GB", MascotLines.Memory(MemoryPressure.Calm, "9.1 GB", 0, false).Line);
    }

    [Fact]
    public void A_preview_never_claims_something_was_deleted()
    {
        Assert.DoesNotContain("清好啦", MascotLines.AiDone("1 GB", preview: true).Line);
        Assert.Contains("什么都没删", MascotLines.AiDone("1 GB", preview: true).Line);
        Assert.DoesNotContain("删好啦", MascotLines.ModelsDone("1 GB", preview: true).Line);
        Assert.Contains("什么都没删", MascotLines.ModelsDone("1 GB", preview: true).Line);
    }

    [Fact]
    public void A_finished_clean_looks_satisfied_and_a_scan_looks_busy()
    {
        Assert.Equal(Mood.Satisfied, MascotLines.AiDone("1 GB", false).Mood);
        Assert.Equal(Mood.Satisfied, MascotLines.ModelsDone("1 GB", false).Mood);
        Assert.Equal(Mood.Busy, MascotLines.AiScanning().Mood);
        Assert.Equal(Mood.Busy, MascotLines.SecurityScanning().Mood);
        Assert.Equal(Mood.Busy, MascotLines.ModelsDeleting().Mood);
    }

    [Theory]
    [InlineData(MemoryPressure.Calm, Mood.Happy)]
    [InlineData(MemoryPressure.Moderate, Mood.Calm)]
    [InlineData(MemoryPressure.High, Mood.Worried)]
    [InlineData(MemoryPressure.Critical, Mood.Worried)]
    public void Memory_mood_follows_pressure(MemoryPressure p, Mood expected) =>
        Assert.Equal(expected, MascotLines.Memory(p, "1 GB", 0, false).Mood);

    [Fact]
    public void A_leak_outranks_calm_pressure()
    {
        var s = MascotLines.Memory(MemoryPressure.Calm, "9 GB", 0, leaking: true);
        Assert.Equal(Mood.Worried, s.Mood);
        Assert.Contains("泄漏", s.Line);
    }

    [Fact]
    public void Idle_apps_are_only_mentioned_when_there_are_some()
    {
        Assert.Contains("3 组", MascotLines.Memory(MemoryPressure.High, "1 GB", 3, false).Line);
        Assert.DoesNotContain("闲着", MascotLines.Memory(MemoryPressure.High, "1 GB", 0, false).Line);
    }

    [Theory]
    [InlineData(2, 0, Mood.Worried)]
    [InlineData(1, 5, Mood.Worried)]
    [InlineData(0, 2, Mood.Calm)]
    [InlineData(0, 0, Mood.Happy)]
    public void Security_mood_follows_findings(int risks, int warns, Mood expected) =>
        Assert.Equal(expected, MascotLines.Security(risks, warns).Mood);

    [Fact]
    public void Security_line_gives_the_count_of_risks() =>
        Assert.Contains("2 件", MascotLines.Security(2, 0).Line);

    [Theory]
    [InlineData(HealthBand.Excellent, Mood.Happy)]
    [InlineData(HealthBand.Good, Mood.Calm)]
    [InlineData(HealthBand.Fair, Mood.Worried)]
    [InlineData(HealthBand.Poor, Mood.Worried)]
    public void Health_mood_follows_the_band(HealthBand band, Mood expected) =>
        Assert.Equal(expected, MascotLines.Health(band).Mood);

    [Fact]
    public void No_line_is_ever_empty()
    {
        var all = new[]
        {
            MascotLines.AiScanning(), MascotLines.AiFound("1 GB", 1), MascotLines.AiNothing(), MascotLines.AiCleaning(),
            MascotLines.AiDone("1 GB", true), MascotLines.AiDone("1 GB", false), MascotLines.Failed("检查"),
            MascotLines.ModelsScanning(), MascotLines.ModelsNone(), MascotLines.ModelsFound("1 GB", 1),
            MascotLines.ModelsSelected(1, "1 GB"), MascotLines.ModelsDeleting(), MascotLines.ModelsDone("1 GB", true),
            MascotLines.ModelsDone("1 GB", false), MascotLines.SecurityScanning(), MascotLines.Security(0, 0),
            MascotLines.Memory(MemoryPressure.Critical, "1 GB", 1, false),
        };
        Assert.All(all, s => Assert.False(string.IsNullOrWhiteSpace(s.Line)));
    }
}
