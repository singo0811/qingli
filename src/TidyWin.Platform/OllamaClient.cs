using System.Net;
using System.Net.Http.Json;
using TidyWin.Core.Memory;

namespace TidyWin.Platform;

/// <summary>Talks to a local Ollama. Only loopback addresses are ever used, whatever OLLAMA_HOST says.</summary>
public sealed class OllamaClient : IOllama
{
    private readonly HttpClient _http;

    public OllamaClient(string? hostSetting = null, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = ResolveBase(hostSetting ?? Environment.GetEnvironmentVariable("OLLAMA_HOST"));
        _http.Timeout = TimeSpan.FromSeconds(3);
    }

    public Uri BaseAddress => _http.BaseAddress!;

    public static Uri ResolveBase(string? setting)
    {
        var fallback = new Uri("http://127.0.0.1:11434/");
        if (string.IsNullOrWhiteSpace(setting)) return fallback;

        var s = setting.Trim();
        if (!s.Contains("://")) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return fallback;
        if (uri.Scheme is not ("http" or "https")) return fallback;

        var host = uri.Host;
        if (host == "0.0.0.0" || host == "[::]" || host == "::") host = "127.0.0.1"; // "listen everywhere" means "reach me locally"
        var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        if (!local) return fallback;

        var port = HasExplicitPort(s) ? uri.Port : 11434; // no port written means Ollama's own default, not 80
        return new UriBuilder(uri.Scheme, host, port).Uri;
    }

    private static bool HasExplicitPort(string withScheme)
    {
        var rest = withScheme[(withScheme.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = rest.IndexOf('/');
        var authority = slash >= 0 ? rest[..slash] : rest;
        var afterBracket = authority.LastIndexOf(']');
        return authority.IndexOf(':', afterBracket + 1) >= 0;
    }

    public async Task<IReadOnlyList<LoadedModel>?> ListLoadedAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.GetAsync("api/ps", ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return OllamaPsParser.Parse(body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>{"model":"...","keep_alive":0}. The name is escaped by the writer, so odd characters cannot break the JSON.</summary>
    internal static string UnloadBody(string model)
    {
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteNumber("keep_alive", 0);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public async Task<bool> UnloadAsync(string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        try
        {
            // Written by hand, not through reflection-based serialization, so it still works after trimming.
            using var content = new StringContent(UnloadBody(model), System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync("api/generate", content, ct).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }
}
