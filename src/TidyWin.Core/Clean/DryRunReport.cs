using System.Text;
using System.Text.Json;
using TidyWin.Core.AiJunk;
using TidyWin.ViewModels;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Clean;

/// <summary>One place a clean-up would touch, as the command line reports it.</summary>
public sealed record DryRunEntry(string Id, string Title, string Tool, string Safety, string Path, int Files, long Bytes, string Consequence);

/// <summary>
/// What a clean-up would free, written for people and for scripts. It is only ever built from scan results:
/// nothing here has a way to delete.
/// </summary>
public sealed record DryRunReport(string Scope, IReadOnlyList<DryRunEntry> Entries, int LeftAlone)
{
    public long TotalBytes => Entries.Sum(e => e.Bytes);

    /// <summary>The part that is rebuilt on its own (the AI list's default selection).</summary>
    public long SafeBytes => Entries.Where(e => e.Safety == nameof(AiSafety.Regenerable)).Sum(e => e.Bytes);

    public static DryRunReport Build(string scope, IEnumerable<AiScanItem> scans)
    {
        var list = scans.ToList();
        var entries = list
            .Where(s => s.Exists && s.Target.CanBeDeleted && s.EligibleBytes > 0)
            .OrderByDescending(s => s.EligibleBytes)
            .Select(s => new DryRunEntry(s.Target.Id, s.Target.Title, s.Target.Tool, s.Target.Safety.ToString(),
                s.Path, s.Eligible.Count, s.EligibleBytes, s.Target.Consequence))
            .ToList();
        var leftAlone = list.Count(s => s.Exists && !s.Target.CanBeDeleted);
        return new DryRunReport(scope, entries, leftAlone);
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        dryRun = true,
        scope = Scope,
        totalBytes = TotalBytes,
        safeBytes = SafeBytes,
        leftAlone = LeftAlone,
        entries = Entries,
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.F("预览(没有删除任何东西) · 范围:{0}",Scope));
        if (Entries.Count == 0)
        {
            sb.AppendLine(Loc.T("没有发现可以清理的东西。"));
            return sb.ToString();
        }

        sb.AppendLine(Loc.F("可释放约 {0},其中会自动重建的 {1}。",ByteSize.Format(TotalBytes),ByteSize.Format(SafeBytes)));
        sb.AppendLine();
        foreach (var e in Entries)
        {
            sb.AppendLine($"{ByteSize.Format(e.Bytes),10}  [{e.Safety}] {e.Title}");
            sb.AppendLine(Loc.F("{0,10}  {1}  ({2:N0} 个文件)","",e.Path,e.Files));
        }

        if (LeftAlone > 0) sb.AppendLine().AppendLine(Loc.F("另有 {0} 处认得但故意不动(里面可能有你的数据)。",LeftAlone));
        return sb.ToString();
    }
}
