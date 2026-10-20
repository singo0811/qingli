using TidyWin.Core.Localization;
using TidyWin.Core.Security;

namespace TidyWin.Core.Tests;

/// <summary>The program-wide language is shared state, so tests that switch it must not run beside any other test.</summary>
[CollectionDefinition("ProgramLanguage", DisableParallelization = true)]
public sealed class ProgramLanguageCollection;

[Collection("ProgramLanguage")]
public class ExitQualityTests
{
    private const string Full = """
        {"ip":"203.0.113.9","is_datacenter":false,"is_vpn":true,"is_proxy":false,"is_tor":false,"is_mobile":false,"is_abuser":false,"is_crawler":false,
         "company":{"name":"Example Net","abuser_score":"0.0004 (Low)"},
         "asn":{"asn":64500,"org":"Example AS","abuser_score":"0.1016 (High)"},
         "location":{"country_code":"US","city":"Seattle","timezone":"America/Los_Angeles"}}
        """;

    private static readonly ExitInfo Exit = new("203.0.113.9", "US", null, "Seattle", "Example Net", "America/Los_Angeles");

    private static ExitQualityInput Input(
        ReputationLookup? rep = null, IReadOnlyList<string>? dns = null, ExitInfo? resolver = null, bool tunnel = true,
        double offsetHours = -8, bool refused = false, bool challenged = false, bool blocked = false,
        ExitHistoryEntry? previous = null, ExitInfo? exit = null) =>
        new(exit ?? Exit, rep ?? new ReputationLookup(ReputationStatus.NoKey), dns ?? [], resolver, tunnel,
            TimeSpan.FromHours(offsetHours), refused, challenged, blocked, previous);

    private static IpReputation Flags(bool dc = false, bool vpn = false, bool proxy = false, bool tor = false, bool abuser = false,
        string? crawler = null, string? label = null) =>
        new("203.0.113.9", dc, vpn, proxy, tor, false, false, abuser, crawler, label, "Example Net", null, "US", "Seattle", "America/Los_Angeles");

    private static ExitQualityResult With(IpReputation r, double offset = -8) =>
        ExitQuality.Assess(Input(new ReputationLookup(ReputationStatus.Ok, r), offsetHours: offset));

    // ───────── parsing ─────────

    [Fact]
    public void Full_answer_is_read_with_nested_company_and_asn()
    {
        var r = ExitQuality.ParseIpapiIs(Full)!;
        Assert.Equal("203.0.113.9", r.Ip);
        Assert.True(r.Vpn);
        Assert.False(r.Datacenter);
        Assert.Null(r.Crawler);                       // is_crawler:false is not a crawler
        Assert.Equal("Low", r.AbuserLabel);           // the company's rating wins over the AS's
        Assert.Equal("Example Net", r.Company);
        Assert.Equal("AS64500 Example AS", r.Asn);
        Assert.Equal("US", r.Country);
        Assert.Equal("America/Los_Angeles", r.TimeZone);
        Assert.True(r.HasFlags);
    }

    [Fact]
    public void Short_answer_without_a_key_has_no_flags()
    {
        var r = ExitQuality.ParseIpapiIs("""{"ip":"198.51.100.4","asn":"AS64501","company":"Some Host","country":"DE"}""")!;
        Assert.False(r.HasFlags);
        Assert.Null(r.Vpn);
        Assert.Equal("AS64501", r.Asn);
        Assert.Equal("Some Host", r.Company);
        Assert.Equal("DE", r.Country);
    }

    [Fact]
    public void A_non_empty_string_in_a_flag_counts_as_true_and_a_crawler_name_is_kept()
    {
        var r = ExitQuality.ParseIpapiIs("""{"ip":"192.0.2.1","is_vpn":"NordVPN","is_crawler":"Googlebot","is_abuser":0}""")!;
        Assert.True(r.Vpn);
        Assert.Equal("Googlebot", r.Crawler);
        Assert.False(r.Abuser);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"error":"x"}""")]
    public void Unusable_answers_give_nothing(string? json) => Assert.Null(ExitQuality.ParseIpapiIs(json));

    [Fact]
    public void Oversized_answer_is_refused() =>
        Assert.Null(ExitQuality.ParseIpapiIs("{\"ip\":\"1.2.3.4\",\"x\":\"" + new string('a', 70_000) + "\"}"));

    [Theory]
    [InlineData("0.1016 (High)", "High")]
    [InlineData("0.0004 (Very Low)", "Very Low")]
    [InlineData("0.5", null)]
    [InlineData(null, null)]
    public void Label_is_read_from_brackets(string? score, string? label) => Assert.Equal(label, ExitQuality.Label(score));

    // ───────── time zones ─────────

    [Theory]
    [InlineData("Asia/Shanghai", 480)]
    [InlineData("America/Los_Angeles", -480)]
    [InlineData("Europe/London", 0)]
    [InlineData("Asia/Kolkata", 330)]
    public void Standard_offset_ignores_daylight_saving(string zone, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), ExitQuality.StandardOffset(zone));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus")]
    public void Unknown_zone_has_no_offset(string? zone) => Assert.Null(ExitQuality.StandardOffset(zone));

    // ───────── judging ─────────

    [Fact]
    public void No_exit_gives_nothing()
    {
        var r = ExitQuality.Assess(Input(exit: null) with { Exit = null });
        Assert.Equal(0, r.Score);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void Clean_address_with_a_key_scores_full_marks()
    {
        var r = With(Flags());
        Assert.Equal(100, r.Score);
        Assert.Contains(r.Findings, f => f.Id == "ainet.q.clean" && f.Severity == Severity.Ok);
        Assert.DoesNotContain(r.Findings, f => f.Id == "ainet.q.nokey");
    }

    [Fact]
    public void Without_a_key_the_flags_are_not_guessed_and_the_user_is_told()
    {
        var r = ExitQuality.Assess(Input());
        Assert.Equal(100, r.Score);                   // nothing known to deduct, so the text must say it is optimistic
        var nokey = Assert.Single(r.Findings, f => f.Id == "ainet.q.nokey");
        Assert.Contains("ipapi.is", nokey.Detail);
        Assert.Contains("ipQualityKey", nokey.Advice);
        Assert.DoesNotContain(r.Findings, f => f.Id == "ainet.q.clean");
    }

    [Fact]
    public void A_failed_lookup_is_not_an_accusation_and_keeps_the_reason()
    {
        var r = ExitQuality.Assess(Input(new ReputationLookup(ReputationStatus.Failed, null, "key rejected")));
        Assert.Equal(100, r.Score);
        var f = Assert.Single(r.Findings, x => x.Id == "ainet.q.failed");
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("key rejected", f.Detail);
    }

    [Theory]
    [InlineData("tor", 50)]
    [InlineData("vpn", 20)]
    [InlineData("proxy", 20)]
    [InlineData("abuser", 30)]
    [InlineData("dc", 20)]
    public void Each_flag_takes_its_own_points(string which, int points)
    {
        var rep = which switch
        {
            "tor" => Flags(tor: true),
            "vpn" => Flags(vpn: true),
            "proxy" => Flags(proxy: true),
            "abuser" => Flags(abuser: true),
            _ => Flags(dc: true),
        };
        Assert.Equal(100 - points, With(rep).Score);
    }

    [Fact]
    public void Crawler_and_a_high_network_rating_take_points_but_a_flagged_abuser_is_not_counted_twice()
    {
        Assert.Equal(90, With(Flags(vpn: false, crawler: "Googlebot", dc: false) with { Vpn = false, Datacenter = false, Tor = false, Abuser = false, Proxy = false }).Score);
        Assert.Equal(90, With(Flags(label: "High")).Score);
        Assert.Equal(70, With(Flags(abuser: true, label: "High")).Score);
    }

    [Fact]
    public void Flags_raise_a_warning_and_tor_a_risk()
    {
        Assert.Equal(Severity.Warn, With(Flags(vpn: true)).Findings.Single(f => f.Id == "ainet.q.flags").Severity);
        Assert.Equal(Severity.Risk, With(Flags(tor: true)).Findings.Single(f => f.Id == "ainet.q.flags").Severity);
    }

    [Fact]
    public void Hosting_name_without_a_key_is_a_marked_guess_with_a_smaller_claim()
    {
        var r = ExitQuality.Assess(Input(exit: Exit with { Org = "DigitalOcean, LLC" }));
        Assert.Equal(80, r.Score);
        Assert.Contains(r.Deductions, d => d.Reason.Contains("推测") || d.Reason.Contains("guess"));
    }

    [Fact]
    public void Service_evidence_outweighs_the_flags()
    {
        Assert.Equal(0, ExitQuality.Assess(Input(refused: true)).Score);
        Assert.Equal(60, ExitQuality.Assess(Input(blocked: true)).Score);
        Assert.Equal(85, ExitQuality.Assess(Input(challenged: true)).Score);
    }

    [Fact]
    public void Score_never_leaves_the_range_and_bands_map_to_severity()
    {
        var worst = With(Flags(tor: true, vpn: true, proxy: true, abuser: true, dc: true));
        Assert.Equal(0, worst.Score);
        Assert.Equal(Severity.Risk, worst.Findings[0].Severity);
        Assert.Equal(Severity.Ok, With(Flags()).Findings[0].Severity);
        Assert.Equal(Severity.Ok, With(Flags(vpn: true)).Findings[0].Severity);                          // 80 is still good
        Assert.Equal(Severity.Info, With(Flags(vpn: true, proxy: true)).Findings[0].Severity);           // 60
        Assert.Equal(Severity.Warn, With(Flags(vpn: true, proxy: true, dc: true)).Findings[0].Severity); // 40
        Assert.Equal(Severity.Risk, With(Flags(tor: true, vpn: true)).Findings[0].Severity);             // 30
    }

    [Fact]
    public void Score_text_says_it_is_our_own_estimate()
    {
        var f = ExitQuality.Assess(Input()).Findings[0];
        Assert.Equal("ainet.q.score", f.Id);
        Assert.Contains("TidyWin", f.Detail);
        Assert.Contains("Anthropic", f.Detail);
    }

    // ───────── DNS ─────────

    private static readonly ExitInfo Cn = new("114.114.114.114", "CN", null, null, "Carrier");

    [Fact]
    public void Dns_in_another_country_behind_a_proxy_is_a_leak()
    {
        var r = ExitQuality.Assess(Input(dns: ["114.114.114.114"], resolver: Cn));
        Assert.Contains(r.Findings, f => f.Id == "ainet.q.dns.leak" && f.Severity == Severity.Warn);
        Assert.Equal(90, r.Score);
    }

    [Fact]
    public void Dns_in_another_country_without_a_proxy_is_only_information()
    {
        var r = ExitQuality.Assess(Input(dns: ["114.114.114.114"], resolver: Cn, tunnel: false));
        Assert.Contains(r.Findings, f => f.Id == "ainet.q.dns.local" && f.Severity == Severity.Info);
        Assert.Equal(100, r.Score);
    }

    [Fact]
    public void Dns_in_the_exit_country_is_fine()
    {
        var us = new ExitInfo("8.8.8.8", "US", null, null, "Resolver");
        var r = ExitQuality.Assess(Input(dns: ["8.8.8.8"], resolver: us));
        Assert.Contains(r.Findings, f => f.Id == "ainet.q.dns.ok" && f.Severity == Severity.Ok);
    }

    [Theory]
    [InlineData("198.18.0.2")]     // fake-ip range
    [InlineData("127.0.0.1")]      // answered locally
    public void Dns_handed_to_the_proxy_is_not_a_leak(string answer)
    {
        var r = ExitQuality.Assess(Input(dns: [answer]));
        Assert.DoesNotContain(r.Findings, f => f.Id.StartsWith("ainet.q.dns"));
        Assert.Equal(100, r.Score);
    }

    // ───────── time zone and history ─────────

    [Fact]
    public void Zone_far_from_the_exit_costs_a_little_and_is_explained()
    {
        var r = ExitQuality.Assess(Input(offsetHours: 8));
        var f = Assert.Single(r.Findings, x => x.Id == "ainet.q.tz");
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal(95, r.Score);
        Assert.Contains("+08:00", f.Detail);
        Assert.Contains("-08:00", f.Detail);
    }

    [Fact]
    public void Zone_close_to_the_exit_is_silent() =>
        Assert.DoesNotContain(ExitQuality.Assess(Input(offsetHours: -7)).Findings, f => f.Id == "ainet.q.tz");

    [Fact]
    public void A_changed_address_is_reported_and_a_repeated_one_is_not()
    {
        var before = new ExitHistoryEntry(DateTime.UtcNow.AddHours(-2), "198.51.100.1", "JP", "Other", 90);
        Assert.Contains(ExitQuality.Assess(Input(previous: before)).Findings, f => f.Id == "ainet.q.changed");
        Assert.DoesNotContain(ExitQuality.Assess(Input(previous: before with { Ip = "203.0.113.9" })).Findings, f => f.Id == "ainet.q.changed");
    }

    [Fact]
    public void History_is_newest_first_capped_and_survives_a_round_trip()
    {
        IReadOnlyList<ExitHistoryEntry> h = [];
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < ExitQuality.MaxHistory + 5; i++)
            h = ExitQuality.AddHistory(h, new ExitHistoryEntry(t0.AddMinutes(i), $"203.0.113.{i % 200 + 1}", "US", "Org 中文", i % 101));
        Assert.Equal(ExitQuality.MaxHistory, h.Count);
        Assert.True(h[0].AtUtc > h[^1].AtUtc);

        var again = ExitQuality.ParseHistory(ExitQuality.WriteHistory(h));
        Assert.Equal(h.Count, again.Count);
        Assert.Equal(h[0], again[0]);
    }

    [Fact]
    public void Damaged_history_is_just_empty_and_bad_entries_are_skipped()
    {
        Assert.Empty(ExitQuality.ParseHistory("{{{"));
        Assert.Empty(ExitQuality.ParseHistory("""{"a":1}"""));
        var list = ExitQuality.ParseHistory("""
            [{"ip":"not an ip","at":"2026-01-01T00:00:00Z","score":5},
             {"ip":"203.0.113.1","at":"yesterday","score":5},
             {"ip":"203.0.113.2","at":"2026-01-01T00:00:00Z","score":900}]
            """);
        var only = Assert.Single(list);
        Assert.Equal(100, only.Score);                // clamped
    }

    [Fact]
    public void Output_is_the_same_in_both_languages_apart_from_words()
    {
        try
        {
            Loc.Use("en", "zh-CN");
            var r = ExitQuality.Assess(Input(new ReputationLookup(ReputationStatus.Ok, Flags(vpn: true)), dns: ["114.114.114.114"], resolver: Cn, offsetHours: 8));
            foreach (var f in r.Findings)
                Assert.DoesNotMatch("[\u4e00-\u9fff]", f.Title + f.Detail + f.Advice);
            Assert.DoesNotMatch("[\u4e00-\u9fff]", r.Text);
        }
        finally { Loc.Use("zh", "zh-CN"); }
    }
}
