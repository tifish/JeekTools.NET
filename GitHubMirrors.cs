using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekTools;

public sealed record GitHubMirrorProbeResult(
    int Index, string Url, bool Ok, double BytesPerSecond, long Bytes, bool TimedOut);

/// <summary>
/// GitHub download mirrors for regions where github.com is slow or blocked:
/// expand a canonical URL into mirror alternatives, and optionally pick one by
/// short throughput probe (<see cref="GetFastestMirror"/>).
/// Preferred-mirror memory for multi-attempt downloads belongs in the caller
/// (e.g. <see cref="AutoUpdater"/>), not in this helper.
/// </summary>
public static class GitHubMirrors
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(GitHubMirrors));
    /// <summary>Maximum bytes to sample from a normal package GET.</summary>
    public const int DefaultProbeBytes = 2 * 1024 * 1024;

    /// <summary>Probes that transfer fewer than this many bytes are treated as failed.</summary>
    public const int DefaultMinUsefulBytes = 256 * 1024;

    public static readonly TimeSpan DefaultPerMirrorTimeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan DefaultProbeCacheTtl = TimeSpan.FromMinutes(10);

    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly object Gate = new();

    /// <summary>Cached throughput-probe winner (-1 = none / expired).</summary>
    private static int _probeMirrorIndex = -1;

    private static DateTime _probeExpiresUtc = DateTime.MinValue;

    public static string[] GetMirrors(string url)
    {
        return
        [
            url,
            url.Replace("https://github.com/", "https://ghfast.top/https://github.com/"),
            url.Replace("https://github.com/", "https://gh-proxy.com/github.com/"),
        ];
    }

    /// <summary>Clears the short-lived throughput probe cache.</summary>
    public static void ResetFastestMirror()
    {
        lock (Gate)
        {
            _probeMirrorIndex = -1;
            _probeExpiresUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Picks a mirror by <b>throughput</b>: each mirror is sampled for up to
    /// <paramref name="probeBytes"/> in parallel (with a per-mirror timeout); the
    /// highest bytes/second wins. Ties prefer lower indices (github.com first).
    /// Results are cached for <see cref="DefaultProbeCacheTtl"/>.
    /// </summary>
    /// <returns>Winning mirror URL, or empty string if every probe failed.</returns>
    public static async Task<string> GetFastestMirror(
        string url,
        int probeBytes = DefaultProbeBytes,
        TimeSpan? perMirrorTimeout = null,
        CancellationToken cancellationToken = default,
        string? userAgent = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(url))
            return "";

        if (probeBytes < DefaultMinUsefulBytes)
            probeBytes = DefaultMinUsefulBytes;

        var mirrors = GetMirrors(url);

        lock (Gate)
        {
            if (_probeMirrorIndex >= 0
                && _probeMirrorIndex < mirrors.Length
                && DateTime.UtcNow < _probeExpiresUtc)
            {
                return mirrors[_probeMirrorIndex];
            }
        }

        var results = await ProbeMirrorsAsync(url, probeBytes, perMirrorTimeout, cancellationToken, userAgent)
            .ConfigureAwait(false);
        foreach (var result in results)
            Log.ZLogInformation($"Update mirror probe: {result.Url}, usable={result.Ok}, bytes={result.Bytes}, speed={result.BytesPerSecond / 1024:0} KB/s, timedOut={result.TimedOut}");

        var best = results
            .Where(r => r.Ok)
            .OrderByDescending(r => r.BytesPerSecond)
            .ThenBy(r => r.Index)
            .FirstOrDefault();

        if (best is null)
            return "";

        lock (Gate)
        {
            _probeMirrorIndex = best.Index;
            _probeExpiresUtc = DateTime.UtcNow.Add(DefaultProbeCacheTtl);
        }

        return mirrors[best.Index];
    }

    /// <summary>Uncached download samples, also exposed for updater diagnostics.
    /// Use normal GETs: Range requests can take a different, slower cache path.
    /// A timed-out sample is useful when enough bytes arrived before the deadline.</summary>
    public static async Task<GitHubMirrorProbeResult[]> ProbeMirrorsAsync(
        string url,
        int probeBytes = DefaultProbeBytes,
        TimeSpan? perMirrorTimeout = null,
        CancellationToken cancellationToken = default,
        string? userAgent = null)
    {
        var mirrors = GetMirrors(url);
        return await Task.WhenAll(mirrors.Select((mirror, index) =>
            ProbeThroughputAsync(mirror, index, Math.Max(probeBytes, DefaultMinUsefulBytes),
                perMirrorTimeout ?? DefaultPerMirrorTimeout, cancellationToken, userAgent: userAgent))).ConfigureAwait(false);
    }

    internal static async Task<GitHubMirrorProbeResult> ProbeThroughputAsync(
        string url,
        int index,
        int probeBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        HttpClient? testClient = null,
        string? userAgent = null)
    {
        var stopwatch = Stopwatch.StartNew();
        long total = 0;
        var timedOut = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            // Infinite client timeout; per-mirror CancelAfter bounds the probe.
            using var client = testClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent ?? DefaultUserAgent);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token)
                .ConfigureAwait(false);

            var buffer = new byte[Math.Min(64 * 1024, probeBytes)];
            while (total < probeBytes)
            {
                var toRead = (int)Math.Min(buffer.Length, probeBytes - total);
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cts.Token)
                    .ConfigureAwait(false);
                if (read <= 0)
                    break;
                total += read;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch
        {
            return new GitHubMirrorProbeResult(index, url, false, 0, total, false);
        }

        var ok = total >= DefaultMinUsefulBytes;
        return new GitHubMirrorProbeResult(index, url, ok,
            ok ? total / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001) : 0, total, timedOut);
    }
}
