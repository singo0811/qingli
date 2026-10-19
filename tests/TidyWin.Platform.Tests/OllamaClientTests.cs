using System.Net;
using System.Text;
using TidyWin.Platform;
using Xunit;

namespace TidyWin.Platform.Tests;

public class OllamaClientTests
{
    [Theory]
    [InlineData(null, "http://127.0.0.1:11434/")]
    [InlineData("", "http://127.0.0.1:11434/")]
    [InlineData("127.0.0.1", "http://127.0.0.1:11434/")]
    [InlineData("127.0.0.1:11500", "http://127.0.0.1:11500/")]
    [InlineData("http://localhost:11434", "http://localhost:11434/")]
    [InlineData("http://localhost", "http://localhost:11434/")]
    [InlineData("0.0.0.0:11434", "http://127.0.0.1:11434/")]
    [InlineData("http://localhost:80", "http://localhost/")]
    public void Local_settings_resolve(string? setting, string expected) =>
        Assert.Equal(expected, OllamaClient.ResolveBase(setting).ToString());

    [Theory]
    [InlineData("192.168.1.20:11434")]
    [InlineData("http://example.com:11434")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("not a url at all")]
    public void Remote_or_odd_settings_fall_back_to_the_local_default(string setting) =>
        Assert.Equal("http://127.0.0.1:11434/", OllamaClient.ResolveBase(setting).ToString());

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Calls = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add((r.Method, r.RequestUri!.AbsolutePath, r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct)));
            return reply(r);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Lists_loaded_models()
    {
        var stub = new Stub(_ => Json("""{"models":[{"name":"llama3:8b","size":100,"size_vram":40}]}"""));
        var list = await new OllamaClient("127.0.0.1", stub).ListLoadedAsync();

        Assert.Equal("llama3:8b", Assert.Single(list!).Name);
        Assert.Equal((HttpMethod.Get, "/api/ps"), (stub.Calls[0].Method, stub.Calls[0].Path));
    }

    [Fact]
    public async Task Unreachable_ollama_is_null_not_an_exception()
    {
        var stub = new Stub(_ => throw new HttpRequestException("refused"));
        Assert.Null(await new OllamaClient("127.0.0.1", stub).ListLoadedAsync());
    }

    [Fact]
    public async Task Unload_posts_keep_alive_zero_for_that_model()
    {
        var stub = new Stub(_ => Json("{}"));
        var ok = await new OllamaClient("127.0.0.1", stub).UnloadAsync("llama3:8b");

        Assert.True(ok);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/generate", call.Path);
        Assert.Contains("\"keep_alive\":0", call.Body);
        Assert.Contains("\"model\":\"llama3:8b\"", call.Body);
    }

    [Fact]
    public async Task Unload_body_stays_valid_json_for_odd_model_names()
    {
        var stub = new Stub(_ => Json("{}"));
        const string odd = "we\"ird\\name:7b\n";
        await new OllamaClient("127.0.0.1", stub).UnloadAsync(odd);

        using var doc = System.Text.Json.JsonDocument.Parse(stub.Calls[0].Body);
        Assert.Equal(odd, doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("keep_alive").GetInt32());
    }

    [Fact]
    public async Task Unload_reports_failure_on_error_status_and_blank_name()
    {
        var bad = new Stub(_ => Json("{}", HttpStatusCode.NotFound));
        var client = new OllamaClient("127.0.0.1", bad);

        Assert.False(await client.UnloadAsync("x"));
        Assert.False(await client.UnloadAsync("  "));
        Assert.Single(bad.Calls); // the blank name never reached the network
    }
}

public class RealMemoryTests
{
    [Fact]
    public void Reads_a_believable_snapshot_of_this_machine()
    {
        var snap = new WindowsMemorySource().Read();

        Assert.InRange(snap.System.TotalBytes, 1L << 30, 8L << 40);
        Assert.InRange(snap.System.AvailableBytes, 1, snap.System.TotalBytes);
        Assert.True(snap.System.CommitLimitBytes >= snap.System.TotalBytes / 2);
        Assert.Contains(snap.Processes, p => p.Pid == Environment.ProcessId);
        Assert.True(snap.Processes.Count > 20);

        var me = snap.Processes.Single(p => p.Pid == Environment.ProcessId);
        Assert.True(me.WorkingSetBytes > 0);
        Assert.NotNull(me.ImagePath);
    }

    [Fact]
    public void One_reading_is_cheap_enough_to_repeat_every_few_seconds()
    {
        var source = new WindowsMemorySource();
        source.Read(); // warm up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) source.Read();
        var avg = sw.Elapsed.TotalMilliseconds / 5;

        Assert.True(avg < 400, $"one reading took {avg:0} ms on average");
    }

    [Fact]
    public void Trimming_a_process_we_own_succeeds()
    {
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul")
        { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Assert.Equal(TidyWin.Core.Memory.TrimOutcome.Trimmed, new WindowsProcessControl().Trim(child.Id));
        }
        finally { try { child.Kill(); } catch { } }
    }

    [Fact]
    public void Trimming_a_process_that_does_not_exist_reports_gone()
    {
        Assert.Equal(TidyWin.Core.Memory.TrimOutcome.Gone, new WindowsProcessControl().Trim(0x7FFFFFF0));
    }
}
