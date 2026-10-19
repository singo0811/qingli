using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.Clean;
using TidyWin.Core.Security;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

public sealed class FindingViewModel
{
    public FindingViewModel(Finding f) => Finding = f;

    public Finding Finding { get; }
    public Severity Severity => Finding.Severity;
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string? Advice => Finding.Advice;
    public bool HasAdvice => Finding.Advice is { Length: > 0 };
}

public sealed record ExposedProgram(string Name, int Port, string Where);

public sealed partial class SecurityViewModel : ObservableObject
{
    private readonly ISecuritySource _source;
    private readonly IAiNetworkSource? _net;

    /// <param name="net">When given, the check also reads proxy settings and reports what they get wrong for AI tools.</param>
    public SecurityViewModel(ISecuritySource source, IAiNetworkSource? net = null)
    {
        _source = source;
        _net = net;
    }

    public ObservableCollection<FindingViewModel> Findings { get; } = [];
    public ObservableCollection<ExposedProgram> Exposed { get; } = [];

    /// <summary>Only this many programs are drawn as tags; the page says how many more there are.</summary>
    public const int ExposedShownMax = 24;
    public ObservableCollection<ExposedProgram> ExposedShown { get; } = [];

    // The same findings, split by how much they matter, so the page can give problems the room and keep "all fine" compact.
    public ObservableCollection<FindingViewModel> Risks { get; } = [];
    public ObservableCollection<FindingViewModel> Warns { get; } = [];
    public ObservableCollection<FindingViewModel> Infos { get; } = [];
    public ObservableCollection<FindingViewModel> Oks { get; } = [];

    [ObservableProperty] private CleanPhase _phase = CleanPhase.Idle;
    [ObservableProperty] private int _riskCount;
    [ObservableProperty] private int _warnCount;
    [ObservableProperty] private int _okCount;
    [ObservableProperty] private string? _error;

    [ObservableProperty] private int _infoCount;

    /// <summary>
    /// What the headline colour should say. A plain note (Info) is not a problem, so only risks and warnings
    /// move this away from Ok.
    /// </summary>
    [ObservableProperty] private Severity _worst = Severity.Ok;

    public string Headline => (RiskCount, WarnCount) switch
    {
        (> 0, _) => Loc.F("{0} 项需要处理",RiskCount),
        (0, > 0) => Loc.F("{0} 项建议留意",WarnCount),
        _ => Loc.T("没有发现需要处理的问题"),
    };

    public async Task ScanAsync()
    {
        if (Phase == CleanPhase.Scanning) return;
        Phase = CleanPhase.Scanning;
        Error = null;

        try
        {
            var (snapshot, listeners, env) = await Task.Run(() => (_source.ReadSnapshot(), _source.ReadListeners(), _net?.ReadEnvironment()));
            var all = new List<Finding>();
            all.AddRange(SystemSecurityAdvisor.Assess(snapshot));
            all.AddRange(NetworkExposure.Assess(listeners, SystemSecurityAdvisor.FirewallConfirmedOn(snapshot)));
            if (env is not null) all.AddRange(AiNetwork.AssessConfig(env, listeners));
            var sorted = SystemSecurityAdvisor.Sort(all);

            Findings.Clear(); Risks.Clear(); Warns.Clear(); Infos.Clear(); Oks.Clear();
            foreach (var f in sorted)
            {
                var item = new FindingViewModel(f);
                Findings.Add(item);
                (f.Severity switch
                {
                    Severity.Risk => Risks,
                    Severity.Warn => Warns,
                    Severity.Info => Infos,
                    _ => Oks,
                }).Add(item);
            }

            Exposed.Clear(); ExposedShown.Clear();
            foreach (var l in NetworkExposure.ExposedPrograms(listeners))
            {
                var p = new ExposedProgram(l.ProcessName, l.Port, l.Scope == BindScope.AnyAddress ? Loc.T("所有网卡") : l.Address);
                Exposed.Add(p);
                if (ExposedShown.Count < ExposedShownMax) ExposedShown.Add(p);
            }

            RiskCount = sorted.Count(f => f.Severity == Severity.Risk);
            WarnCount = sorted.Count(f => f.Severity == Severity.Warn);
            OkCount = sorted.Count(f => f.Severity == Severity.Ok);
            InfoCount = sorted.Count(f => f.Severity == Severity.Info);
            Worst = RiskCount > 0 ? Severity.Risk : WarnCount > 0 ? Severity.Warn : Severity.Ok;
            OnPropertyChanged(nameof(Headline));
            Phase = CleanPhase.Ready;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Phase = CleanPhase.Idle;
        }
    }
}
