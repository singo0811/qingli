using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TidyWin.Core.Update;

namespace TidyWin.Platform;

public enum UpdateState { NotConfigured, UpToDate, Available, Failed }

public sealed record UpdateCheck(UpdateState State, UpdateInfo? Info = null, string? Problem = null);

/// <summary>
/// Looks for a newer version at one address the person (or the installer) configured, downloads it only after they
/// agree, checks its SHA-256 against the feed, and hands it to the installer. Nothing is contacted until an address
/// is configured, nothing is sent except the plain request, and nothing is installed without a click.
/// </summary>
public sealed class Updater
{
    private readonly HttpClient _http;
    private readonly string _folder;

    public Updater(HttpMessageHandler? handler = null, string? folder = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TidyWin-Updater");
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWin", "update");
    }

    /// <summary>The feed address: an environment variable for testing, else the saved setting. Empty means "not set up".</summary>
    public static string FeedUrl(AppSettings settings) =>
        Environment.GetEnvironmentVariable("TIDYWIN_UPDATE_FEED") is { Length: > 0 } fromEnv ? fromEnv : settings.UpdateFeed;

    /// <summary>The version of the program that is running (the executable's), not of this library.</summary>
    public static Version CurrentVersion() =>
        (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly).GetName().Version is { } v
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0))
            : new Version(0, 0, 0);

    public async Task<UpdateCheck> CheckAsync(string feedUrl, Version current, string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl)) return new UpdateCheck(UpdateState.NotConfigured);
        if (!UpdateManifest.TryHttps(feedUrl, out var uri)) return new UpdateCheck(UpdateState.Failed, Problem: "the feed address must be https");

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new UpdateCheck(UpdateState.Failed, Problem: $"HTTP {(int)response.StatusCode}");
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" }) return new UpdateCheck(UpdateState.Failed, Problem: "redirected away from https");

            var text = await ReadLimitedAsync(response, UpdateManifest.MaxFeedBytes, ct).ConfigureAwait(false);
            if (text is null) return new UpdateCheck(UpdateState.Failed, Problem: "the feed is too large");

            var info = UpdateManifest.Parse(text, language, out var problem);
            if (info is null) return new UpdateCheck(UpdateState.Failed, Problem: problem);

            return UpdateManifest.IsNewer(current, info.Version)
                ? new UpdateCheck(UpdateState.Available, info)
                : new UpdateCheck(UpdateState.UpToDate, info);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return new UpdateCheck(UpdateState.Failed, Problem: e.Message);
        }
    }

    /// <summary>
    /// Downloads to a temporary name, stops if more bytes arrive than the feed promised, and only renames to the final
    /// name when the SHA-256 matches. A mismatch deletes the file. Returns the path of the verified installer.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_folder);
        var name = $"TidyWin-Setup-{info.Version}.exe";
        var final = Path.Combine(_folder, name);
        var part = final + ".part";

        try
        {
            using var response = await _http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" }) throw new InvalidDataException("redirected away from https");

            using var sha = SHA256.Create();
            long total = 0;
            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > info.Size) throw new InvalidDataException("the download is larger than the feed said");
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    progress?.Report((double)total / info.Size);
                }
                sha.TransformFinalBlock([], 0, 0);
            }

            if (total != info.Size) throw new InvalidDataException("the download is incomplete");
            var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (!string.Equals(actual, info.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("the download does not match its checksum");

            File.Move(part, final, overwrite: true);
            return final;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>Runs the verified installer. It asks running copies of the program to close and starts the new one afterwards.</summary>
    public static bool Launch(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(installerPath, "/SILENT /CLOSEAPPLICATIONS /NOCANCEL") { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Removes installers left over from earlier updates.</summary>
    public void CleanOld()
    {
        try
        {
            if (!Directory.Exists(_folder)) return;
            foreach (var f in Directory.EnumerateFiles(_folder, "TidyWin-Setup-*")) TryDelete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leftovers are harmless.
        }
    }

    private static async Task<string?> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var s = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int n;
        while ((n = await s.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > limit) return null;
            ms.Write(buffer, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* left for CleanOld */ }
    }
}
