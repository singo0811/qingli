using TidyWin.Core.Clean;
using TidyWin.Core.Security;
using TidyWin.ViewModels;
using Xunit;

namespace TidyWin.Core.Tests;

public class DefenderJsonTests
{
    [Fact]
    public void Reads_the_fields_we_ask_for()
    {
        var d = SystemSecurityAdvisor.ParseDefenderJson(
            """{"RealTimeProtectionEnabled":true,"AntivirusEnabled":true,"AntivirusSignatureAge":2,"AMRunningMode":"Normal","IsTamperProtected":false}""");

        Assert.NotNull(d);
        Assert.True(d!.RealTimeProtection);
        Assert.Equal(2, d.SignatureAgeDays);
        Assert.Equal("Normal", d.RunningMode);
        Assert.False(d.TamperProtected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    [InlineData("""{"Something":"else"}""")]
    public void Anything_unusable_is_null_never_a_guess(string? json) =>
        Assert.Null(SystemSecurityAdvisor.ParseDefenderJson(json));

    [Fact]
    public void A_missing_field_stays_unknown_instead_of_false()
    {
        var d = SystemSecurityAdvisor.ParseDefenderJson("""{"RealTimeProtectionEnabled":true}""");
        Assert.True(d!.RealTimeProtection);
        Assert.Null(d.TamperProtected);
        Assert.Null(d.SignatureAgeDays);
    }
}

public class SystemSecurityAdvisorTests
{
    private static SecuritySnapshot Good(
        DefenderStatus? defender = null, bool? fwD = true, bool? fwP = true, bool? fwU = true,
        bool? uac = true, string? hosts = "# nothing\n127.0.0.1 localhost\n", ProxySetting? proxy = null) =>
        new(defender ?? new DefenderStatus(true, 0, true, "Normal", true), fwD, fwP, fwU, uac, hosts,
            proxy ?? new ProxySetting(false, null, null));

    private static Finding One(SecuritySnapshot s, string id) => Assert.Single(SystemSecurityAdvisor.Assess(s), f => f.Id == id);
    private static bool Has(SecuritySnapshot s, string id) => SystemSecurityAdvisor.Assess(s).Any(f => f.Id == id);

    [Fact]
    public void A_healthy_machine_has_nothing_above_ok()
    {
        var all = SystemSecurityAdvisor.Assess(Good());
        Assert.DoesNotContain(all, f => f.Severity > Severity.Ok);
        Assert.Contains(all, f => f.Id == "defender.ok");
    }

    // ───── Defender ─────

    [Fact] public void Realtime_off_is_a_risk() =>
        Assert.Equal(Severity.Risk, One(Good(new DefenderStatus(false, 0, true, "Normal", true)), "defender.realtime.off").Severity);

    [Fact] public void Antivirus_disabled_is_a_risk() =>
        Assert.Equal(Severity.Risk, One(Good(new DefenderStatus(true, 0, false, "Normal", true)), "defender.realtime.off").Severity);

    [Theory]
    [InlineData(0, Severity.Ok)]
    [InlineData(6, Severity.Ok)]
    [InlineData(7, Severity.Warn)]
    [InlineData(29, Severity.Warn)]
    [InlineData(30, Severity.Risk)]
    public void Signature_age_has_clear_edges(int days, Severity expected)
    {
        var all = SystemSecurityAdvisor.Assess(Good(new DefenderStatus(true, days, true, "Normal", true)));
        var f = Assert.Single(all, x => x.Id.StartsWith("defender.") && x.Id != "defender.tamper.off");
        Assert.Equal(expected, f.Severity);
    }

    [Fact]
    public void Unreadable_defender_is_unknown_not_fine()
    {
        var f = One(Good() with { Defender = null }, "defender.unknown");
        Assert.Equal(Severity.Info, f.Severity);
        Assert.False(Has(Good() with { Defender = null }, "defender.ok"));
    }

    [Fact]
    public void Passive_mode_means_someone_else_protects_and_we_do_not_judge_it()
    {
        var s = Good(new DefenderStatus(false, 400, false, "Passive Mode", true));
        Assert.Equal(Severity.Info, One(s, "defender.passive").Severity);
        Assert.False(Has(s, "defender.realtime.off"));
        Assert.False(Has(s, "defender.signature.stale"));
    }

    [Fact]
    public void Tamper_protection_off_is_only_a_note() =>
        Assert.Equal(Severity.Info, One(Good(new DefenderStatus(true, 0, true, "Normal", false)), "defender.tamper.off").Severity);

    [Fact]
    public void Defender_on_but_realtime_unknown_is_not_reported_as_ok()
    {
        var s = Good(new DefenderStatus(null, 1, null, "Normal", true));
        Assert.False(Has(s, "defender.ok"));
        Assert.True(Has(s, "defender.unknown"));
    }

    // ───── firewall / UAC ─────

    [Fact]
    public void A_firewall_profile_that_is_off_names_that_profile()
    {
        var f = One(Good(fwU: false), "firewall.off");
        Assert.Equal(Severity.Risk, f.Severity);
        Assert.Contains("公用网络", f.Title);
        Assert.DoesNotContain("专用网络", f.Title);
    }

    [Fact] public void All_profiles_on_is_ok() => Assert.Equal(Severity.Ok, One(Good(), "firewall.ok").Severity);

    [Fact]
    public void Some_unknown_profiles_are_info_not_ok()
    {
        var s = Good(fwP: null);
        Assert.Equal(Severity.Info, One(s, "firewall.unknown").Severity);
        Assert.False(Has(s, "firewall.ok"));
    }

    [Fact] public void Firewall_confirmed_requires_every_profile() =>
        Assert.False(SystemSecurityAdvisor.FirewallConfirmedOn(Good(fwP: null)));

    [Fact] public void Uac_off_is_a_risk() => Assert.Equal(Severity.Risk, One(Good(uac: false), "uac.off").Severity);
    [Fact] public void Uac_unknown_says_nothing() => Assert.DoesNotContain(SystemSecurityAdvisor.Assess(Good(uac: null)), f => f.Id.StartsWith("uac."));

    // ───── hosts ─────

    [Fact]
    public void Hosts_parser_skips_comments_blanks_and_the_names_docker_adds_on_purpose()
    {
        const string text = """
            # Copyright
            127.0.0.1       localhost
            ::1             localhost

            192.168.1.5 host.docker.internal gateway.docker.internal  # Added by Docker Desktop
            0.0.0.0 ads.example.com tracker.example.com
            10.1.1.1 intranet.corp   # comment
            garbage line
            notanip example.org
            """;

        var e = SystemSecurityAdvisor.ParseHosts(text);
        Assert.Equal(
            ["ads.example.com", "tracker.example.com", "intranet.corp"],
            e.Select(x => x.HostName).ToArray());
    }

    [Fact]
    public void Hosts_redirects_are_a_warning_and_blocks_are_only_info()
    {
        var redirect = Good(hosts: "10.9.9.9 bank.example.com\n");
        Assert.Equal(Severity.Warn, One(redirect, "hosts.redirect").Severity);
        Assert.Contains("bank.example.com → 10.9.9.9", One(redirect, "hosts.redirect").Detail);

        var block = Good(hosts: "0.0.0.0 ads.example.com\n127.0.0.1 t.example.com\n");
        Assert.Equal(Severity.Info, One(block, "hosts.block").Severity);
        Assert.False(Has(block, "hosts.redirect"));
    }

    [Fact] public void Clean_hosts_is_ok() => Assert.Equal(Severity.Ok, One(Good(), "hosts.ok").Severity);
    [Fact] public void Unreadable_hosts_is_info() => Assert.Equal(Severity.Info, One(Good(hosts: null), "hosts.unreadable").Severity);

    // ───── proxy ─────

    [Theory]
    [InlineData("127.0.0.1:7890", new[] { "127.0.0.1" })]
    [InlineData("http=127.0.0.1:80;https=10.0.0.5:8080", new[] { "127.0.0.1", "10.0.0.5" })]
    [InlineData("http://proxy.corp:3128", new[] { "proxy.corp" })]
    [InlineData("[::1]:7890", new[] { "::1" })]
    [InlineData("localhost", new[] { "localhost" })]
    public void Proxy_hosts_are_extracted_from_every_form(string server, string[] expected) =>
        Assert.Equal(expected, SystemSecurityAdvisor.ProxyHosts(server));

    [Fact]
    public void A_remote_proxy_is_a_warning_even_when_a_local_one_is_listed_beside_it() =>
        Assert.Equal(Severity.Warn, One(Good(proxy: new ProxySetting(true, "http=127.0.0.1:80;https=10.0.0.5:8080", null)), "proxy.remote").Severity);

    [Fact]
    public void A_local_proxy_is_only_info()
    {
        var s = Good(proxy: new ProxySetting(true, "127.0.0.1:7890", null));
        Assert.Equal(Severity.Info, One(s, "proxy.local").Severity);
        Assert.False(Has(s, "proxy.remote"));
    }

    [Fact] public void A_disabled_proxy_is_ok() => Assert.Equal(Severity.Ok, One(Good(), "proxy.none").Severity);

    [Fact]
    public void A_pac_script_is_noted_and_does_not_count_as_no_proxy()
    {
        var s = Good(proxy: new ProxySetting(false, null, "http://x/proxy.pac"));
        Assert.Equal(Severity.Info, One(s, "proxy.pac").Severity);
        Assert.False(Has(s, "proxy.none"));
    }

    [Fact]
    public void Results_come_back_most_severe_first()
    {
        var all = SystemSecurityAdvisor.Assess(Good(new DefenderStatus(false, 0, true, "Normal", true), fwU: false, hosts: "10.0.0.1 x.com\n"));
        var levels = all.Select(f => f.Severity).ToList();
        Assert.Equal(levels.OrderByDescending(l => l).ToList(), levels);
        Assert.Equal(Severity.Risk, levels[0]);
    }
}

public class NetworkExposureTests
{
    private static Listener L(string addr, int port, string proc, int pid = 1000, string? path = @"C:\Users\Ann\AppData\Local\Programs\x.exe") =>
        new(addr, port, pid, proc, path);

    [Theory]
    [InlineData("127.0.0.1", BindScope.Loopback)]
    [InlineData("127.5.5.5", BindScope.Loopback)]
    [InlineData("::1", BindScope.Loopback)]
    [InlineData("0.0.0.0", BindScope.AnyAddress)]
    [InlineData("::", BindScope.AnyAddress)]
    [InlineData("192.168.1.20", BindScope.Specific)]
    [InlineData("fe80::1", BindScope.Specific)]
    [InlineData("garbage", BindScope.Specific)]
    public void Scope_of_an_address(string address, BindScope expected) =>
        Assert.Equal(expected, Listener.ScopeOf(address));

    [Fact]
    public void Ollama_open_to_the_network_is_a_risk_when_the_firewall_is_not_confirmed()
    {
        var f = Assert.Single(NetworkExposure.Assess([L("0.0.0.0", 11434, "ollama")], firewallConfirmedOn: false), x => x.Id.StartsWith("net.ai.ollama"));
        Assert.Equal(Severity.Risk, f.Severity);
        Assert.Contains("11434", f.Title);
        Assert.NotNull(f.Advice);
        Assert.Contains("OLLAMA_HOST", f.Advice);
    }

    [Fact]
    public void A_confirmed_firewall_lowers_the_alarm_but_does_not_remove_it()
    {
        var f = Assert.Single(NetworkExposure.Assess([L("0.0.0.0", 11434, "ollama")], firewallConfirmedOn: true), x => x.Id.StartsWith("net.ai.ollama"));
        Assert.Equal(Severity.Warn, f.Severity);
        Assert.Contains("允许访问", f.Detail);
    }

    [Fact]
    public void Ollama_on_loopback_is_fine()
    {
        var all = NetworkExposure.Assess([L("127.0.0.1", 11434, "ollama")], false);
        Assert.Equal("net.ai.ok", Assert.Single(all, f => f.Id.StartsWith("net.ai")).Id);
    }

    [Fact]
    public void The_same_service_on_ipv4_and_ipv6_is_reported_once()
    {
        var all = NetworkExposure.Assess([L("0.0.0.0", 11434, "ollama"), L("::", 11434, "ollama")], false);
        Assert.Single(all, f => f.Id.StartsWith("net.ai.ollama"));
        Assert.Single(NetworkExposure.FindAiServices([L("0.0.0.0", 11434, "ollama"), L("::", 11434, "ollama")]).Single().Listeners);
    }

    [Fact]
    public void A_generic_runtime_on_a_gradio_port_is_a_maybe_not_a_certainty()
    {
        var m = Assert.Single(NetworkExposure.FindAiServices([L("0.0.0.0", 7860, "python")]));
        Assert.False(m.Sure);
        var f = Assert.Single(NetworkExposure.Assess([L("0.0.0.0", 7860, "python")], false), x => x.Id.StartsWith("net.ai.gradio"));
        Assert.StartsWith("可能", f.Title);
    }

    [Fact]
    public void An_unrelated_program_on_a_non_distinctive_port_is_not_called_an_ai_service()
    {
        Assert.Empty(NetworkExposure.FindAiServices([L("0.0.0.0", 7860, "notepad")]));
        Assert.Empty(NetworkExposure.FindAiServices([L("0.0.0.0", 8080, "chrome")]));
        Assert.Empty(NetworkExposure.FindAiServices([L("0.0.0.0", 8888, "slack")]));
    }

    [Fact]
    public void A_distinctive_port_is_enough_whoever_holds_it()
    {
        var m = Assert.Single(NetworkExposure.FindAiServices([L("0.0.0.0", 11434, "com.docker.backend")]));
        Assert.Equal("Ollama", m.Service.Name);
        Assert.False(m.Sure); // matched by the port only
    }

    [Fact]
    public void A_known_name_is_certain_even_on_another_port()
    {
        var m = Assert.Single(NetworkExposure.FindAiServices([L("0.0.0.0", 9999, "llama-server.exe")]));
        Assert.True(m.Sure);
        Assert.Equal("llama.cpp 服务", m.Service.Name);
    }

    [Fact]
    public void Jupyter_stays_a_warning_because_it_has_a_token_by_default()
    {
        var f = Assert.Single(NetworkExposure.Assess([L("0.0.0.0", 8888, "python")], firewallConfirmedOn: false), x => x.Id.StartsWith("net.ai.jupyter"));
        Assert.Equal(Severity.Warn, f.Severity);
    }

    [Fact]
    public void Exposed_programs_leave_out_windows_services_protected_processes_and_loopback()
    {
        var exposed = NetworkExposure.ExposedPrograms(
        [
            L("0.0.0.0", 135, "svchost", 800, @"C:\Windows\System32\svchost.exe"),
            L("0.0.0.0", 445, "System", 4, null),
            L("0.0.0.0", 5040, "mystery", 900, null),
            L("127.0.0.1", 9000, "dev", 1100),
            L("0.0.0.0", 27036, "steam", 1200, @"C:\Program Files (x86)\Steam\steam.exe"),
            L("::", 27036, "steam", 1200, @"C:\Program Files (x86)\Steam\steam.exe"),
        ]);

        var one = Assert.Single(exposed);
        Assert.Equal("steam", one.ProcessName);
    }

    [Fact]
    public void Other_listeners_are_summarised_without_repeating_the_ai_service()
    {
        var all = NetworkExposure.Assess(
            [L("0.0.0.0", 11434, "ollama", 10), L("0.0.0.0", 27036, "steam", 11, @"C:\Games\steam.exe")], false);

        var other = Assert.Single(all, f => f.Id == "net.other");
        Assert.Contains("steam:27036", other.Detail);
        Assert.DoesNotContain("ollama", other.Detail);
        Assert.Equal(Severity.Info, other.Severity);
    }
}

public class SecurityViewModelTests
{
    private sealed class Fake(SecuritySnapshot snap, IReadOnlyList<Listener> listeners) : ISecuritySource
    {
        public int Reads;
        public SecuritySnapshot ReadSnapshot() { Reads++; return snap; }
        public IReadOnlyList<Listener> ReadListeners() => listeners;
    }

    private static SecuritySnapshot Snap(bool fwPublic = true) => new(
        new DefenderStatus(true, 0, true, "Normal", true), true, true, fwPublic, true, "", new ProxySetting(false, null, null));

    [Fact]
    public async Task A_clean_machine_reads_as_clean()
    {
        var vm = new SecurityViewModel(new Fake(Snap(), []));
        await vm.ScanAsync();

        Assert.Equal(CleanPhase.Ready, vm.Phase);
        Assert.Equal(0, vm.RiskCount);
        Assert.Equal(0, vm.WarnCount);
        Assert.Equal(Severity.Ok, vm.Worst);
        Assert.Equal("没有发现需要处理的问题", vm.Headline);
    }

    [Fact]
    public async Task Problems_are_counted_sorted_and_shown_first()
    {
        var listeners = new[] { new Listener("0.0.0.0", 11434, 10, "ollama", @"C:\x\ollama.exe") };
        var vm = new SecurityViewModel(new Fake(Snap(fwPublic: false), listeners));
        await vm.ScanAsync();

        Assert.True(vm.RiskCount >= 2); // firewall off + Ollama exposed
        Assert.Equal(Severity.Risk, vm.Worst);
        Assert.Equal($"{vm.RiskCount} 项需要处理", vm.Headline);
        Assert.Equal(Severity.Risk, vm.Findings[0].Severity);
        Assert.Equal(vm.Findings.Select(f => f.Severity).OrderByDescending(s => s), vm.Findings.Select(f => f.Severity));
        Assert.Contains(vm.Exposed, e => e.Name == "ollama" && e.Port == 11434 && e.Where == "所有网卡");
    }

    [Fact]
    public async Task Findings_are_split_into_groups_that_add_up_to_the_whole()
    {
        var listeners = new[] { new Listener("0.0.0.0", 11434, 10, "ollama", @"C:\x\ollama.exe") };
        var vm = new SecurityViewModel(new Fake(Snap(fwPublic: false), listeners));
        await vm.ScanAsync();

        Assert.Equal(vm.Findings.Count, vm.Risks.Count + vm.Warns.Count + vm.Infos.Count + vm.Oks.Count);
        Assert.All(vm.Risks, f => Assert.Equal(Severity.Risk, f.Severity));
        Assert.All(vm.Warns, f => Assert.Equal(Severity.Warn, f.Severity));
        Assert.All(vm.Infos, f => Assert.Equal(Severity.Info, f.Severity));
        Assert.All(vm.Oks, f => Assert.Equal(Severity.Ok, f.Severity));
        Assert.Equal(vm.RiskCount, vm.Risks.Count);
        Assert.Equal(vm.InfoCount, vm.Infos.Count);
    }

    [Fact]
    public async Task A_plain_note_does_not_turn_the_verdict_into_a_problem()
    {
        // An unrelated program listening outside gives an Info finding; that must not colour the whole verdict.
        var listeners = new[] { new Listener("0.0.0.0", 27036, 11, "steam", @"C:\Games\steam.exe") };
        var vm = new SecurityViewModel(new Fake(Snap(), listeners));
        await vm.ScanAsync();

        Assert.True(vm.InfoCount >= 1);
        Assert.Equal(0, vm.RiskCount + vm.WarnCount);
        Assert.Equal(Severity.Ok, vm.Worst);
        Assert.Equal("没有发现需要处理的问题", vm.Headline);
    }

    [Fact]
    public async Task Only_a_limited_number_of_exposed_programs_are_drawn_but_all_are_counted()
    {
        var listeners = Enumerable.Range(0, SecurityViewModel.ExposedShownMax + 7)
            .Select(i => new Listener("0.0.0.0", 40000 + i, 100 + i, "app" + i, @"C:\Apps\app.exe"))
            .ToList();
        var vm = new SecurityViewModel(new Fake(Snap(), listeners));
        await vm.ScanAsync();

        Assert.Equal(SecurityViewModel.ExposedShownMax + 7, vm.Exposed.Count);
        Assert.Equal(SecurityViewModel.ExposedShownMax, vm.ExposedShown.Count);
    }

    [Fact]
    public async Task A_second_scan_replaces_the_first_instead_of_piling_up()
    {
        var src = new Fake(Snap(), []);
        var vm = new SecurityViewModel(src);
        await vm.ScanAsync();
        var first = vm.Findings.Count;
        await vm.ScanAsync();

        Assert.Equal(first, vm.Findings.Count);
        Assert.Equal(2, src.Reads);
    }

    [Fact]
    public async Task A_failing_source_ends_in_idle_with_a_reason()
    {
        var vm = new SecurityViewModel(new Boom());
        await vm.ScanAsync();

        Assert.Equal(CleanPhase.Idle, vm.Phase);
        Assert.Equal("boom", vm.Error);
    }

    private sealed class Boom : ISecuritySource
    {
        public SecuritySnapshot ReadSnapshot() => throw new InvalidOperationException("boom");
        public IReadOnlyList<Listener> ReadListeners() => [];
    }
}
