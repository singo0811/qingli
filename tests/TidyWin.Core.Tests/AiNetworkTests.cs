using System.Net;
using TidyWin.Core.Security;
using TidyWin.ViewModels;

namespace TidyWin.Core.Tests;

public class AiNetworkTests
{
    private static readonly AiEndpoint Api = new("api", "Tool A", "Some AI", "api.example.com", "/", "chat", true);
    private static readonly AiEndpoint Pkg = new("pkg", "Tool B", "Some Registry", "pkg.example.com", "/", "install", false);

    private static ProbeOutcome Ok(int ms = 120) => new(ProbeKind.Ok, 200, ms);
    private static ProbeOutcome Bad(ProbeKind k) => new(k);

    private static NetworkEnv Env(string? system = null, string? https = null, params string[] adapters) =>
        new(system is null ? new ProxySetting(false, null, null) : new ProxySetting(true, system, null),
            null, https, null, null, adapters);

    private static Listener Listen(int port) => new("127.0.0.1", port, 100, "clash", @"C:\Tools\clash.exe");

    private static EndpointCheck Check(AiEndpoint e, ProbeOutcome direct, ProbeOutcome? proxy = null, params string[] ips) =>
        new(e, direct, proxy, ips);

    // ───────── reading addresses ─────────

    [Theory]
    [InlineData("127.0.0.1:7890", "127.0.0.1", 7890)]
    [InlineData("http://127.0.0.1:7890", "127.0.0.1", 7890)]
    [InlineData("http://user:pw@proxy.corp:8080/", "proxy.corp", 8080)]
    [InlineData("socks5://[::1]:1080", "::1", 1080)]
    public void Proxy_addresses_are_read_in_every_common_shape(string text, string host, int port) =>
        Assert.Equal((host, port), AiNetwork.ParseEndpoint(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-port")]
    [InlineData("host:99999")]
    public void Nonsense_is_not_an_address(string? text) => Assert.Null(AiNetwork.ParseEndpoint(text));

    [Fact]
    public void A_per_protocol_system_setting_yields_each_distinct_target_once()
    {
        var t = AiNetwork.SystemProxyTargets("http=127.0.0.1:7890;https=127.0.0.1:7890;socks=127.0.0.1:7891");
        Assert.Equal([("127.0.0.1", 7890), ("127.0.0.1", 7891)], t);
    }

    [Fact]
    public void Credentials_in_a_proxy_address_are_noticed_but_a_plain_port_is_not()
    {
        Assert.True(AiNetwork.HasCredentials("http://me:secret@127.0.0.1:7890"));
        Assert.False(AiNetwork.HasCredentials("http://127.0.0.1:7890"));
        Assert.False(AiNetwork.HasCredentials(null));
    }

    [Theory]
    [InlineData("198.18.0.5", true, false)]   // fake-ip is normal for proxy tools
    [InlineData("127.0.0.1", false, true)]
    [InlineData("0.0.0.0", false, true)]
    [InlineData("192.168.1.8", false, true)]
    [InlineData("104.18.7.1", false, false)]
    public void Fake_ip_is_told_apart_from_an_impossible_answer(string ip, bool fake, bool impossible)
    {
        var a = IPAddress.Parse(ip);
        Assert.Equal(fake, AiNetwork.IsFakeIp(a));
        Assert.Equal(impossible, AiNetwork.IsImpossibleForPublicSite(a));
    }

    // ───────── settings only ─────────

    [Fact]
    public void A_system_proxy_nobody_is_listening_on_is_called_out()
    {
        var f = AiNetwork.AssessConfig(Env("127.0.0.1:7890"), [Listen(1234)]);
        var dead = Assert.Single(f, x => x.Id == "ainet.proxy.dead");
        Assert.Equal(Severity.Warn, dead.Severity);
        Assert.Contains("7890", dead.Detail);
    }

    [Fact]
    public void A_running_proxy_is_not_reported_dead_and_terminal_tools_get_a_heads_up()
    {
        var f = AiNetwork.AssessConfig(Env("127.0.0.1:7890"), [Listen(7890)]);
        Assert.DoesNotContain(f, x => x.Id == "ainet.proxy.dead");
        Assert.Contains(f, x => x.Id == "ainet.env.missing" && x.Severity == Severity.Info);
    }

    [Fact]
    public void The_terminal_heads_up_is_dropped_once_an_environment_proxy_or_a_tunnel_covers_it()
    {
        Assert.DoesNotContain(AiNetwork.AssessConfig(Env("127.0.0.1:7890", "http://127.0.0.1:7890"), [Listen(7890)]), x => x.Id == "ainet.env.missing");
        Assert.DoesNotContain(AiNetwork.AssessConfig(Env("127.0.0.1:7890", null, "Meta Tunnel"), [Listen(7890)]), x => x.Id == "ainet.env.missing");
    }

    [Fact]
    public void A_dead_environment_proxy_and_a_password_in_it_are_each_reported()
    {
        var f = AiNetwork.AssessConfig(Env(null, "http://me:pw@127.0.0.1:7899"), []);
        Assert.Contains(f, x => x.Id == "ainet.env.dead");
        Assert.Contains(f, x => x.Id == "ainet.env.secret");
    }

    [Fact]
    public void A_proxy_on_another_machine_is_not_treated_as_a_dead_local_one()
    {
        var f = AiNetwork.AssessConfig(Env("proxy.corp:8080"), []);
        Assert.DoesNotContain(f, x => x.Id == "ainet.proxy.dead");
    }

    [Fact]
    public void A_tunnel_adapter_is_mentioned()
    {
        Assert.Contains(AiNetwork.AssessConfig(Env(null, null, "Meta Tunnel"), []), x => x.Id == "ainet.tun");
    }

    // ───────── what the vendors' own documentation says goes wrong ─────────

    [Fact]
    public void The_endpoint_list_covers_the_hosts_the_vendors_document_for_sign_in_and_inference()
    {
        var hosts = AiNetwork.Endpoints.Select(e => e.Host).ToList();
        foreach (var h in new[] { "api.anthropic.com", "claude.ai", "platform.claude.com", "auth.openai.com", "chatgpt.com", "api.openai.com" })
            Assert.Contains(h, hosts);
        Assert.Equal(hosts.Count, hosts.Distinct().Count());
    }

    [Theory]
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("socks5h://127.0.0.1:1080")]
    public void A_socks_proxy_is_called_out_because_claude_code_does_not_support_it(string value)
    {
        var env = new NetworkEnv(null, null, null, value, null, []);
        var f = Assert.Single(AiNetwork.AssessConfig(env, [Listen(1080)]), x => x.Id == "ainet.socks");
        Assert.Equal(Severity.Warn, f.Severity);
        Assert.Contains("Claude Code", f.Title);
    }

    [Fact]
    public void A_proxy_address_without_a_scheme_is_called_out_but_a_full_one_is_not()
    {
        Assert.Contains(AiNetwork.AssessConfig(Env(null, "127.0.0.1:7890"), [Listen(7890)]), x => x.Id == "ainet.scheme");
        Assert.DoesNotContain(AiNetwork.AssessConfig(Env(null, "http://127.0.0.1:7890"), [Listen(7890)]), x => x.Id == "ainet.scheme");
    }

    [Fact]
    public void A_certificate_variable_pointing_nowhere_is_a_warning_and_a_present_one_is_a_note()
    {
        var env = new NetworkEnv(null, null, null, null, null, [])
        {
            CertFiles = [new CertFile("SSL_CERT_FILE", @"C:\old\bundle.pem", false), new CertFile("NODE_EXTRA_CA_CERTS", @"C:\ca.pem", true)],
        };
        var f = AiNetwork.AssessConfig(env, []);
        Assert.Contains(f, x => x.Id == "ainet.cert.missing" && x.Severity == Severity.Warn && x.Detail.Contains("SSL_CERT_FILE"));

        var present = new NetworkEnv(null, null, null, null, null, []) { CertFiles = [new CertFile("SSL_CERT_FILE", @"C:\ca.pem", true)] };
        Assert.Contains(AiNetwork.AssessConfig(present, []), x => x.Id == "ainet.cert.custom" && x.Severity == Severity.Info);
    }

    [Fact]
    public void A_proxy_kept_in_a_tools_own_file_is_checked_against_what_is_really_listening()
    {
        var env = new NetworkEnv(null, null, null, null, null, [])
        {
            ToolProxies = [new ToolProxy("Codex", @"~\.codex\.env", "HTTPS_PROXY", "http://127.0.0.1:6850")],
        };

        var dead = Assert.Single(AiNetwork.AssessConfig(env, [Listen(7892)]), x => x.Id.StartsWith("ainet.toolproxy.dead"));
        Assert.Contains("Codex", dead.Title);
        Assert.Contains("6850", dead.Detail);
        Assert.Empty(AiNetwork.AssessConfig(env, [Listen(6850)]).Where(x => x.Id.StartsWith("ainet.toolproxy.dead")));
    }

    [Fact]
    public void The_dead_environment_proxy_message_names_the_codex_symptom()
    {
        var f = AiNetwork.AssessConfig(Env(null, "http://127.0.0.1:6850"), [Listen(7892)]);
        Assert.Contains("token_exchange_failed", f.Single(x => x.Id == "ainet.env.dead").Detail);
    }

    [Fact]
    public void No_proxy_entries_that_cover_ai_hosts_are_noticed_only_when_a_proxy_is_in_use()
    {
        var withProxy = new NetworkEnv(new ProxySetting(true, "127.0.0.1:7890", null), null, null, null, ".openai.com,localhost", []);
        Assert.Contains(AiNetwork.AssessConfig(withProxy, [Listen(7890)]), x => x.Id == "ainet.noproxy" && x.Detail.Contains("api.openai.com"));

        var noProxy = new NetworkEnv(null, null, null, null, ".openai.com", []);
        Assert.DoesNotContain(AiNetwork.AssessConfig(noProxy, []), x => x.Id == "ainet.noproxy");
    }

    [Fact]
    public void A_human_check_page_is_reported_as_that_and_the_site_still_counts_as_reached()
    {
        var f = AiNetwork.AssessChecks(Env(), [Check(Api, new ProbeOutcome(ProbeKind.Challenged, 403, 300))]);

        Assert.Contains(f, x => x.Id == "ainet.challenge" && x.Severity == Severity.Warn);
        Assert.DoesNotContain(f, x => x.Id is "ainet.broken" or "ainet.ok");
        Assert.True(new ProbeOutcome(ProbeKind.Challenged).Reached);
    }

    [Fact]
    public void A_human_check_only_counts_on_real_api_paths_not_on_web_front_doors()
    {
        Assert.True(AiNetwork.ChallengeIsMeaningful(AiNetwork.Endpoints.Single(e => e.Host == "api.openai.com")));
        Assert.True(AiNetwork.ChallengeIsMeaningful(AiNetwork.Endpoints.Single(e => e.Host == "api.anthropic.com")));
        Assert.False(AiNetwork.ChallengeIsMeaningful(AiNetwork.Endpoints.Single(e => e.Host == "chatgpt.com")));
        Assert.False(AiNetwork.ChallengeIsMeaningful(AiNetwork.Endpoints.Single(e => e.Host == "auth.openai.com")));
        Assert.False(AiNetwork.ChallengeIsMeaningful(AiNetwork.Endpoints.Single(e => e.Host == "claude.ai")));
    }

    // ───────── bans and rate limits ─────────

    [Theory]
    [InlineData(403, "error code: 1020", ProbeKind.Blocked)]
    [InlineData(403, "<title>Attention Required!</title> Sorry, you have been blocked", ProbeKind.Blocked)]
    [InlineData(403, "error code: 1006", ProbeKind.Blocked)]
    [InlineData(429, "", ProbeKind.RateLimited)]
    public void Refusals_that_mean_the_address_is_blocked_or_limited_are_told_apart(int status, string body, ProbeKind expected) =>
        Assert.Equal(expected, AiNetwork.ClassifyRefusal(Api, status, body));

    [Fact]
    public void An_ordinary_refusal_or_the_client_signature_block_is_not_blamed_on_the_address()
    {
        Assert.Null(AiNetwork.ClassifyRefusal(Api, 403, "forbidden"));
        Assert.Null(AiNetwork.ClassifyRefusal(Api, 403, "error code: 1010")); // about the client, not the address
        Assert.Null(AiNetwork.ClassifyRefusal(Api, 401, "error code: 1020"));
    }

    [Fact]
    public void Region_wording_wins_over_the_generic_block_page()
    {
        var e = new AiEndpoint("x", "T", "X", "x.example.com", "/v1", "u", true, "unsupported_country");
        Assert.Equal(ProbeKind.RegionBlocked, AiNetwork.ClassifyRefusal(e, 403, "unsupported_country error code: 1020"));
    }

    [Fact]
    public void A_blocked_address_is_a_risk_and_a_limited_one_a_warning_and_both_still_count_as_reached()
    {
        Assert.True(new ProbeOutcome(ProbeKind.Blocked).Reached);
        Assert.True(new ProbeOutcome(ProbeKind.RateLimited).Reached);

        var f = AiNetwork.AssessChecks(Env(), [Check(Api, new ProbeOutcome(ProbeKind.Blocked, 403, 200))]);
        Assert.Contains(f, x => x.Id == "ainet.blocked" && x.Severity == Severity.Risk);
        Assert.DoesNotContain(f, x => x.Id is "ainet.broken" or "ainet.ok");

        var l = AiNetwork.AssessChecks(Env(), [Check(Api, new ProbeOutcome(ProbeKind.RateLimited, 429, 200))]);
        Assert.Contains(l, x => x.Id == "ainet.ratelimit" && x.Severity == Severity.Warn);
        Assert.DoesNotContain(l, x => x.Id == "ainet.ok");
    }

    // ───────── what the services' own edges say they see ─────────

    [Fact]
    public void The_trace_text_is_read_for_address_country_and_colo()
    {
        var t = AiNetwork.ParseTrace("fl=1f\nh=chatgpt.com\nip=160.16.78.98\nts=1\nloc=JP\ncolo=NRT\nwarp=off\n");
        Assert.Equal(("160.16.78.98", "JP", "NRT"), t);
        Assert.Equal("2407:cdc0::1", AiNetwork.ParseTrace("ip=2407:cdc0::1\nloc=SG\n")!.Value.Ip);
        Assert.Null(AiNetwork.ParseTrace("loc=JP\n"));
        Assert.Null(AiNetwork.ParseTrace("ip=not-an-address\nloc=JP\n"));
        Assert.Null(AiNetwork.ParseTrace(null));
        Assert.Null(AiNetwork.ParseTrace("ip=1.2.3.4\nloc=XX\n")!.Value.Country);
    }

    private static HostTrace Tr(string host, TraceFamily f, string? ip, string? cc, bool proxy = false, string tool = "Codex / ChatGPT") =>
        new(tool, host, f, proxy, ip, cc, "XXX");

    [Fact]
    public void A_site_seeing_an_unsupported_country_is_a_warning_naming_the_site()
    {
        var f = AiNetwork.AssessTraces(Env(), [Tr("chatgpt.com", TraceFamily.Default, "1.2.3.4", "HK")]);
        var w = Assert.Single(f, x => x.Id == "ainet.trace.region");
        Assert.Equal(Severity.Warn, w.Severity);
        Assert.Contains("chatgpt.com", w.Title);
        Assert.DoesNotContain(f, x => x.Id == "ainet.trace.ok");
    }

    [Fact]
    public void Ipv4_and_ipv6_leaving_from_different_countries_is_the_split_that_gets_accounts_flagged()
    {
        var f = AiNetwork.AssessTraces(Env(),
        [
            Tr("api.openai.com", TraceFamily.V4, "160.16.78.98", "JP"),
            Tr("api.openai.com", TraceFamily.V6, "2407:cdc0::1", "SG"),
            Tr("api.openai.com", TraceFamily.Default, "160.16.78.98", "JP"),
        ]);
        var w = Assert.Single(f, x => x.Id == "ainet.trace.family");
        Assert.Equal(Severity.Warn, w.Severity);
        Assert.Contains("JP", w.Detail);
        Assert.Contains("SG", w.Detail);
    }

    [Fact]
    public void Ipv4_and_ipv6_in_the_same_country_are_fine()
    {
        var f = AiNetwork.AssessTraces(Env(),
        [
            Tr("api.openai.com", TraceFamily.V4, "1.1.1.1", "SG"),
            Tr("api.openai.com", TraceFamily.V6, "2407:cdc0::1", "SG"),
        ]);
        Assert.DoesNotContain(f, x => x.Id == "ainet.trace.family");
        Assert.Contains(f, x => x.Id == "ainet.trace.ok");
    }

    [Fact]
    public void Sites_of_one_tool_seeing_different_countries_is_called_out_as_a_routing_gap()
    {
        var f = AiNetwork.AssessTraces(Env(),
        [
            Tr("auth.openai.com", TraceFamily.Default, "2407:cdc0::1", "SG"),
            Tr("chatgpt.com", TraceFamily.Default, "160.16.78.98", "JP"),
        ]);
        var w = Assert.Single(f, x => x.Id == "ainet.trace.split");
        Assert.Equal(Severity.Warn, w.Severity);
        Assert.Contains("auth.openai.com", w.Advice);
    }

    [Fact]
    public void One_country_with_mixed_address_types_is_only_a_note_and_other_tools_are_not_mixed_in()
    {
        var f = AiNetwork.AssessTraces(Env(),
        [
            Tr("auth.openai.com", TraceFamily.Default, "2407:cdc0::1", "SG"),
            Tr("chatgpt.com", TraceFamily.Default, "1.2.3.4", "SG"),
            Tr("claude.ai", TraceFamily.Default, "9.9.9.9", "US", tool: "Claude Code"),
        ]);
        Assert.Equal(Severity.Info, Assert.Single(f, x => x.Id == "ainet.trace.mixed").Severity);
        Assert.DoesNotContain(f, x => x.Id == "ainet.trace.split");
    }

    [Fact]
    public void Direct_and_proxied_views_that_differ_are_reported_as_information()
    {
        var f = AiNetwork.AssessTraces(Env("127.0.0.1:7890"),
        [
            Tr("api.openai.com", TraceFamily.Default, "1.1.1.1", "SG"),
            Tr("api.openai.com", TraceFamily.Default, "2.2.2.2", "US", proxy: true),
        ]);
        Assert.Equal(Severity.Info, Assert.Single(f, x => x.Id == "ainet.trace.proxydiff").Severity);
    }

    [Fact]
    public void When_no_site_answers_the_trace_it_says_so_instead_of_claiming_all_is_well()
    {
        var f = AiNetwork.AssessTraces(Env(), [Tr("chatgpt.com", TraceFamily.Default, null, null)]);
        Assert.Single(f);
        Assert.Equal("ainet.trace.none", f[0].Id);
    }

    [Fact]
    public async Task The_check_runs_every_trace_target_in_three_ways_and_adds_the_proxy_view_when_configured()
    {
        var plain = new FakeNet(Env(), (_, _) => Ok());
        var vm = new AiNetworkViewModel(plain);
        await vm.CheckAsync();
        Assert.Contains("chatgpt.com", vm.TraceText);
        Assert.Contains("IPv6 —", vm.TraceText);
        Assert.DoesNotContain("按电脑的代理设置", vm.TraceText);

        var calls = new List<(string, TraceFamily, bool)>();
        var withProxy = new FakeNet(Env("127.0.0.1:7890"), (_, _) => Ok()) { Trace = (h, f, p) => { lock (calls) calls.Add((h, f, p)); return ("1.1.1.1", "US"); } };
        var vm2 = new AiNetworkViewModel(withProxy);
        await vm2.CheckAsync();
        Assert.Equal(AiNetwork.TraceTargets.Count * 4, calls.Count);
        Assert.Equal(AiNetwork.TraceTargets.Count, calls.Count(c => c.Item3));
        Assert.Contains("按电脑的代理设置", vm2.TraceText);
    }

    // ───────── where the connection comes from ─────────

    private static ExitInfo Exit(string ip, string country, string org) => new(ip, country, null, "City", org);

    [Fact]
    public void A_different_exit_for_direct_and_proxied_traffic_is_flagged_and_stronger_when_the_country_differs()
    {
        var sameCountry = AiNetwork.AssessExit(Env(), Exit("1.1.1.1", "US", "AS1 Home"), Exit("2.2.2.2", "US", "AS2 Home"), "US");
        Assert.Equal(Severity.Info, sameCountry.Single(x => x.Id == "ainet.exit.split").Severity);

        var other = AiNetwork.AssessExit(Env(), Exit("1.1.1.1", "CN", "AS1 Home"), Exit("2.2.2.2", "US", "AS2 Home"), "CN");
        Assert.Equal(Severity.Warn, other.Single(x => x.Id == "ainet.exit.split").Severity);
    }

    [Fact]
    public void One_exit_gives_no_split_warning()
    {
        var f = AiNetwork.AssessExit(Env(), Exit("1.1.1.1", "US", "AS1 Home"), Exit("1.1.1.1", "US", "AS1 Home"), "US");
        Assert.DoesNotContain(f, x => x.Id == "ainet.exit.split");
        Assert.Contains(f, x => x.Id == "ainet.exit");
    }

    [Theory]
    [InlineData("AS14061 DigitalOcean, LLC", true)]
    [InlineData("AS16509 Amazon.com, Inc.", true)]
    [InlineData("AS4134 CHINANET-BACKBONE", false)]
    [InlineData("AS7922 Comcast Cable Communications", false)]
    [InlineData(null, false)]
    public void Data_centre_networks_are_told_apart_from_home_lines(string? org, bool hosting) =>
        Assert.Equal(hosting, AiNetwork.LooksLikeHosting(org));

    [Fact]
    public void A_data_centre_exit_is_only_a_note_that_says_what_it_does_and_does_not_prove()
    {
        var f = AiNetwork.AssessExit(Env(), null, Exit("9.9.9.9", "US", "AS14061 DigitalOcean, LLC"), "US");
        var h = Assert.Single(f, x => x.Id == "ainet.exit.hosting");
        Assert.Equal(Severity.Info, h.Severity);
        Assert.Contains("不能说明账号", h.Detail);
    }

    [Fact]
    public void An_exit_in_another_country_than_the_region_setting_states_the_providers_policy_instead_of_scoring_it()
    {
        var f = AiNetwork.AssessExit(Env(), null, Exit("9.9.9.9", "US", "AS1 Home"), "CN");
        var c = Assert.Single(f, x => x.Id == "ainet.exit.country");
        Assert.Equal(Severity.Info, c.Severity);
        Assert.Contains("不能让不受支持地区的使用变得合规", c.Detail);

        Assert.DoesNotContain(AiNetwork.AssessExit(Env(), null, Exit("9.9.9.9", "CN", "AS1 Home"), "CN"), x => x.Id == "ainet.exit.country");
    }

    [Fact]
    public void An_exit_that_could_not_be_looked_up_is_said_plainly()
    {
        Assert.Contains(AiNetwork.AssessExit(Env(), null, null, "CN"), x => x.Id == "ainet.exit.unknown");
    }

    [Fact]
    public async Task The_check_reports_both_exits_when_a_proxy_is_set_and_one_when_it_is_not()
    {
        var withProxy = new FakeNet(Env("127.0.0.1:7890"), (_, _) => Ok())
        {
            Exit = proxy => proxy ? new ExitInfo("2.2.2.2", "US", null, "Austin", "AS2 Home") : new ExitInfo("1.1.1.1", "CN", null, "Beijing", "AS4134 CHINANET"),
        };
        var vm = new AiNetworkViewModel(withProxy);
        await vm.CheckAsync();
        Assert.Contains("1.1.1.1", vm.ExitText);
        Assert.Contains("2.2.2.2", vm.ExitText);
        Assert.Contains(vm.Findings, f => f.Finding.Id == "ainet.exit.split");

        var plain = new AiNetworkViewModel(new FakeNet(Env(), (_, _) => Ok()));
        await plain.CheckAsync();
        Assert.Contains("203.0.113.9", plain.ExitText);
        Assert.DoesNotContain(plain.Findings, f => f.Finding.Id == "ainet.exit.split");
    }

    // ───────── live results ─────────

    [Fact]
    public void When_everything_connects_the_answer_is_a_single_good_note()
    {
        var f = AiNetwork.AssessChecks(Env(), [Check(Api, Ok()), Check(Pkg, Ok())]);
        var only = Assert.Single(f);
        Assert.Equal(Severity.Ok, only.Severity);
        Assert.Equal("ainet.ok", only.Id);
    }

    [Fact]
    public void A_site_that_only_opens_through_the_proxy_warns_when_terminal_tools_have_nothing_to_follow()
    {
        var f = AiNetwork.AssessChecks(Env("127.0.0.1:7890"),
            [Check(Api, Bad(ProbeKind.Timeout), Ok()), Check(Pkg, Ok(), Ok())]);

        var need = Assert.Single(f, x => x.Id == "ainet.needproxy");
        Assert.Equal(Severity.Warn, need.Severity);
        Assert.Contains("Some AI", need.Title);
        Assert.DoesNotContain("Some Registry", need.Title);
        Assert.Contains("HTTPS_PROXY", need.Advice);
    }

    [Fact]
    public void The_same_result_is_only_a_note_when_an_environment_proxy_exists()
    {
        var f = AiNetwork.AssessChecks(Env("127.0.0.1:7890", "http://127.0.0.1:7890"), [Check(Api, Bad(ProbeKind.Timeout), Ok())]);
        Assert.Equal(Severity.Info, f.Single(x => x.Id == "ainet.needproxy").Severity);
    }

    [Fact]
    public void A_site_that_fails_both_ways_is_a_problem_with_the_reason_in_plain_words()
    {
        var f = AiNetwork.AssessChecks(Env("127.0.0.1:7890"), [Check(Api, Bad(ProbeKind.Timeout), Bad(ProbeKind.ProxyFailed))]);

        var broken = Assert.Single(f, x => x.Id == "ainet.broken");
        Assert.Equal(Severity.Warn, broken.Severity);
        Assert.Contains("代理", broken.Detail);
        Assert.DoesNotContain(f, x => x.Id == "ainet.ok");
    }

    [Fact]
    public void With_no_proxy_set_the_advice_is_to_start_the_proxy_tool()
    {
        var f = AiNetwork.AssessChecks(Env(), [Check(Api, Bad(ProbeKind.Timeout))]);
        var broken = Assert.Single(f, x => x.Id == "ainet.broken");
        Assert.Contains("没有设置代理", broken.Title);
        Assert.Contains("代理软件", broken.Advice);
    }

    [Fact]
    public void A_missing_package_registry_is_only_a_note_but_a_missing_api_is_a_warning()
    {
        Assert.Equal(Severity.Info, AiNetwork.AssessChecks(Env(), [Check(Pkg, Bad(ProbeKind.DnsFailed))]).Single(x => x.Id == "ainet.broken").Severity);
        Assert.Equal(Severity.Warn, AiNetwork.AssessChecks(Env(), [Check(Api, Bad(ProbeKind.DnsFailed))]).Single(x => x.Id == "ainet.broken").Severity);
    }

    [Fact]
    public void A_region_refusal_is_reported_as_that_and_not_as_a_broken_network()
    {
        var f = AiNetwork.AssessChecks(Env(), [Check(Api, new ProbeOutcome(ProbeKind.RegionBlocked, 403, 200))]);

        Assert.Contains(f, x => x.Id == "ainet.region");
        Assert.DoesNotContain(f, x => x.Id is "ainet.broken" or "ainet.ok");
    }

    [Fact]
    public void An_impossible_dns_answer_is_a_warning_and_fake_ip_is_only_a_note()
    {
        var bad = AiNetwork.AssessChecks(Env(), [Check(Api, Ok(), null, "127.0.0.1")]);
        Assert.Contains(bad, x => x.Id == "ainet.dns.bad" && x.Severity == Severity.Warn);

        var fake = AiNetwork.AssessChecks(Env(), [Check(Api, Ok(), null, "198.18.0.9")]);
        Assert.Contains(fake, x => x.Id == "ainet.dns.fakeip" && x.Severity == Severity.Info);
        Assert.DoesNotContain(fake, x => x.Id == "ainet.dns.bad");
    }

    [Fact]
    public void A_slow_but_working_connection_is_mentioned()
    {
        Assert.Contains(AiNetwork.AssessChecks(Env(), [Check(Api, Ok(5200))]), x => x.Id == "ainet.slow");
    }

    // ───────── the view-model ─────────

    private sealed class FakeNet(NetworkEnv env, Func<AiEndpoint, bool, ProbeOutcome> probe) : IAiNetworkSource
    {
        public List<(string Host, bool Proxy)> Calls { get; } = [];
        public NetworkEnv ReadEnvironment() => env;

        public Task<ProbeOutcome> ProbeAsync(AiEndpoint e, bool follow, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((e.Host, follow));
            return Task.FromResult(probe(e, follow));
        }

        public Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["104.18.7.1"]);

        public Func<bool, ExitInfo?> Exit { get; set; } = _ => new ExitInfo("203.0.113.9", "US", null, "Austin", "AS1 Home Broadband");
        public Task<ExitInfo?> LookupExitAsync(bool followComputerProxy, CancellationToken ct = default) => Task.FromResult(Exit(followComputerProxy));
        public string? SystemCountry() => "CN";

        public Func<string, TraceFamily, bool, (string Ip, string Country)?> Trace { get; set; } = (_, f, _) => f == TraceFamily.V6 ? null : ("203.0.113.9", "US");
        public Task<HostTrace> TraceAsync(string tool, string host, TraceFamily family, bool viaProxy, CancellationToken ct = default) =>
            Task.FromResult(Trace(host, family, viaProxy) is { } t ? new HostTrace(tool, host, family, viaProxy, t.Ip, t.Country, "XXX") : new HostTrace(tool, host, family, viaProxy, null, null, null));
    }

    [Fact]
    public async Task Without_a_proxy_each_site_is_tried_once_and_only_directly()
    {
        var net = new FakeNet(Env(), (_, _) => Ok());
        var vm = new AiNetworkViewModel(net);

        await vm.CheckAsync();

        Assert.Equal(NetPhase.Ready, vm.Phase);
        Assert.Equal(AiNetwork.Endpoints.Count, vm.Rows.Count);
        Assert.Equal(AiNetwork.Endpoints.Count, vm.Done);
        Assert.All(net.Calls, c => Assert.False(c.Proxy));
        Assert.All(vm.Rows, r => Assert.Equal(3, r.ProxyLevel));
        Assert.Equal(0, vm.Problems);
    }

    [Fact]
    public async Task With_a_proxy_each_site_is_tried_both_ways_and_the_split_shows_in_the_rows_and_findings()
    {
        // Direct fails for the AI APIs, the proxy reaches everything.
        var net = new FakeNet(Env("127.0.0.1:7890"), (e, proxy) => proxy || !e.IsApi ? Ok() : Bad(ProbeKind.Timeout));
        var vm = new AiNetworkViewModel(net);

        await vm.CheckAsync();

        Assert.Equal(AiNetwork.Endpoints.Count * 2, net.Calls.Count);
        var openai = vm.Rows.Single(r => r.Name == "OpenAI 接口");
        Assert.Equal(2, openai.DirectLevel);
        Assert.Equal(0, openai.ProxyLevel);
        Assert.Contains(vm.Findings, f => f.Finding.Id == "ainet.needproxy");
        Assert.True(vm.Problems >= 1);
        Assert.Contains("系统代理", vm.Setup);
    }

    [Fact]
    public async Task A_second_check_while_one_runs_does_nothing()
    {
        var net = new FakeNet(Env(), (_, _) => Ok());
        var vm = new AiNetworkViewModel(net);
        vm.Phase = NetPhase.Checking; // as if a check were already running

        await vm.CheckAsync();

        Assert.Empty(net.Calls);
    }

    [Fact]
    public async Task The_security_check_reports_the_proxy_settings_when_given_a_network_source()
    {
        var net = new FakeNet(Env("127.0.0.1:7890"), (_, _) => Ok());
        var vm = new SecurityViewModel(new EmptySecurity(), net);

        await vm.ScanAsync();

        Assert.Contains(vm.Findings, f => f.Finding.Id == "ainet.proxy.dead");
    }

    private sealed class EmptySecurity : ISecuritySource
    {
        public SecuritySnapshot ReadSnapshot() => new(null, null, null, null, null, "", null);
        public IReadOnlyList<Listener> ReadListeners() => [];
    }
}
