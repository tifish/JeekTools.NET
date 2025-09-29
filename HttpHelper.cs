using System.Net;
using System.Net.Http.Headers;

namespace JeekTools;

public static class HttpHelper
{
    public class HttpHeaders(HttpContentHeaders responseHeaders)
    {
        public DateTime? LastModified => GetDateTime("Last-Modified");
        public long FileSize => GetInt("Content-Length") ?? -1L;

        public string? GetFileName()
        {
            return responseHeaders.ContentDisposition?.FileName?.Trim('"');
        }

        public DateTime? GetDateTime(string header)
        {
            if (!responseHeaders.TryGetValues(header, out var values))
                return null;

            var dateString = values.FirstOrDefault();

            if (DateTime.TryParse(dateString, out var lastModified))
                return lastModified;

            return null;
        }

        public int? GetInt(string header)
        {
            if (!responseHeaders.TryGetValues(header, out var values))
                return null;

            var lengthString = values.FirstOrDefault();
            if (int.TryParse(lengthString, out var length))
                return length;

            return null;
        }
    }

    public static async Task<HttpHeaders?> GetHeaders(string url)
    {
        var respondHeaders = await GetHttpContentHeaders(url);
        if (respondHeaders == null)
            return null;

        return new HttpHeaders(respondHeaders);
    }

    public static HttpClient GetHttpClient(int timeoutSeconds = 5)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        // act like a Chrome browser
        client.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3"
        );
        return client;
    }

    private static async Task<HttpContentHeaders?> GetHttpContentHeaders(string url)
    {
        try
        {
            using var response = await GetHttpHeadResponse(url);
            return response?.Content.Headers;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<HttpResponseMessage?> GetHttpHeadResponse(string url)
    {
        try
        {
            using var client = GetHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await client.SendAsync(request);
            if (response == null)
                return null;

            if (response.StatusCode == HttpStatusCode.Redirect)
            {
                var redirectUrl = response.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(redirectUrl))
                    return null;

                response.Dispose();

                return await GetHttpHeadResponse(redirectUrl);
            }

            return response;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task<string?> DownloadFile(
        string url,
        string downloadDirectory,
        Action<double>? progressCallback = null
    )
    {
        // Use GET request to download the file
        using var client = GetHttpClient();
        using var getResponse = await client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead
        );
        if (getResponse == null || !getResponse.IsSuccessStatusCode)
            return null;

        // Use GET response header to get file name and size
        var getHeaders = new HttpHeaders(getResponse.Content.Headers);
        var fileName = getHeaders.GetFileName();
        if (string.IsNullOrEmpty(fileName))
            fileName = Path.GetFileName(url);
        var filePath = Path.Combine(downloadDirectory, fileName);

        var totalBytes = getHeaders.FileSize;
        var canReportProgress = totalBytes != -1L;

        // Download file
        var buffer = new byte[8 * 1024 * 1024]; // 8MB
        long totalRead = 0;
        int read;
        using (var contentStream = await getResponse.Content.ReadAsStreamAsync())
        using (
            var fileStream = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            )
        )
        {
            while (
                (read = await contentStream.ReadAsync(buffer.AsMemory(), CancellationToken.None))
                > 0
            )
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;
                if (canReportProgress && totalBytes > 0)
                {
                    double progress = (double)totalRead / totalBytes * 100;
                    progressCallback?.Invoke(progress);
                }
            }
        }

        return filePath;
    }
}
