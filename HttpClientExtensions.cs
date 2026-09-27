namespace JeekTools;

public static class HttpClientExtensions
{
    public static async Task DownloadAsync(
        this HttpClient client,
        string requestUri,
        Stream destination,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        // Get the http headers first to examine the content length
        using var response = await client.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Failed to download {requestUri}");

        var contentLength = response.Content.Headers.ContentLength;

        await using var download = await response.Content.ReadAsStreamAsync(cancellationToken);
        // Ignore progress reporting when no progress reporter was
        // passed or when the content length is unknown
        if (progress == null || !contentLength.HasValue)
        {
            await download.CopyToAsync(destination, cancellationToken);
            progress?.Report(1);
            return;
        }

        // Convert absolute progress (bytes downloaded) into relative progress (0% - 100%)
        var relativeProgress = new RelativeDownloadProgress(progress, contentLength.Value);
        // Use extension method to report progress while downloading
        await download.CopyToAsync(destination, 81920, relativeProgress, cancellationToken);
        progress.Report(1);
    }

    // 仅转换进度数值，避免每个数据块都额外排队到线程池，造成回调积压和乱序。
    private sealed class RelativeDownloadProgress(IProgress<float> progress, long contentLength)
        : IProgress<long>
    {
        public void Report(long totalBytes) => progress.Report((float)totalBytes / contentLength);
    }

    public static async Task<bool> TestUrl(
        this HttpClient httpClient,
        string url,
        int timeoutMilliseconds = 3000
    )
    {
        var timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        if (httpClient.Timeout != timeout)
            httpClient.Timeout = timeout;

        using var request = new HttpRequestMessage(HttpMethod.Head, url);

        try
        {
            using var response = await httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
