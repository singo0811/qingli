using System.Text.Json;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;

namespace TidyWin.Core.Tests;

public class DryRunReportTests
{
    private static AiScanItem Scan(string id, AiSafety safety, long eligible, bool exists = true)
    {
        var target = new AiTarget(id, "Tool", "Title " + id, AiCategory.AppCache, safety, KnownFolder.LocalAppData,
            @"X\" + id, [], "comes back", null);
        var files = eligible > 0 ? new List<FsFile> { new(@"C:\X\a.bin", eligible, DateTime.UtcNow.AddDays(-30), false) } : [];
        return new AiScanItem(new ResolvedAiTarget(target, @"C:\X\" + id), exists, files.Count, eligible, files, eligible);
    }

    [Fact]
    public void Only_places_that_would_really_free_something_are_listed_biggest_first()
    {
        var r = DryRunReport.Build("all",
        [
            Scan("small", AiSafety.Regenerable, 100),
            Scan("big", AiSafety.ReDownloadable, 5000),
            Scan("empty", AiSafety.Regenerable, 0),
            Scan("missing", AiSafety.Regenerable, 0, exists: false),
        ]);

        Assert.Equal(["big", "small"], r.Entries.Select(e => e.Id));
        Assert.Equal(5100, r.TotalBytes);
        Assert.Equal(100, r.SafeBytes); // only what rebuilds itself counts as the safe part
    }

    [Fact]
    public void Places_that_are_never_touched_are_counted_but_not_offered()
    {
        var r = DryRunReport.Build("ai", [Scan("keep", AiSafety.Never, 9000), Scan("cache", AiSafety.Regenerable, 10)]);

        Assert.Equal(["cache"], r.Entries.Select(e => e.Id));
        Assert.Equal(1, r.LeftAlone);
        Assert.Contains("故意不动", r.ToText());
    }

    [Fact]
    public void The_text_says_nothing_was_deleted_and_json_says_it_was_a_dry_run()
    {
        var r = DryRunReport.Build("system", [Scan("a", AiSafety.Regenerable, 2048)]);

        Assert.Contains("没有删除任何东西", r.ToText());
        using var doc = JsonDocument.Parse(r.ToJson());
        Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2048, doc.RootElement.GetProperty("totalBytes").GetInt64());
        Assert.Equal("a", doc.RootElement.GetProperty("entries")[0].GetProperty("Id").GetString());
    }

    [Fact]
    public void A_clean_machine_gets_a_plain_sentence()
    {
        Assert.Contains("没有发现", DryRunReport.Build("all", []).ToText());
    }
}
