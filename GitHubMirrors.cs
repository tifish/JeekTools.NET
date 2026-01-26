namespace JeekTools;

public static class GitHubMirrors
{
    public static string[] GetMirrors(string url)
    {
        return
        [
            url,
            url.Replace("https://github.com/", "https://ghfast.top/https://github.com/"),
            url.Replace("https://github.com/", "https://gh-proxy.com/github.com/"),
        ];
    }

    public static async Task<string> GetFastestMirror(string url)
    {
        if (_fastestMirrorIndex == -1)
        {
            if (TestUrl == "")
                TestUrl = url;

            if (!await TestMirrorSpeed())
                return "";
        }

        return GetMirrors(url)[_fastestMirrorIndex];
    }

    public static string TestUrl { get; set; } = "";

    private static int _fastestMirrorIndex = -1;

    public static void ResetFastestMirror()
    {
        _fastestMirrorIndex = -1;
    }

    private static async Task<bool> TestMirrorSpeed()
    {
        _fastestMirrorIndex = -1;

        var mirrors = GetMirrors(TestUrl);
        var ctsList = mirrors.Select(_ => new CancellationTokenSource()).ToList();

        // Test mirror speed
        var tasks = mirrors
            .Select(
                (mirror, idx) =>
                {
                    var cts = ctsList[idx];
                    return Task.Run(
                        async () =>
                        {
                            try
                            {
                                using var client = HttpHelper.GetHttpClient();
                                var request = new HttpRequestMessage(HttpMethod.Get, mirror);
                                request.Headers.Range =
                                    new System.Net.Http.Headers.RangeHeaderValue(0, 102399);

                                using var response = await client.SendAsync(
                                    request,
                                    HttpCompletionOption.ResponseHeadersRead,
                                    cts.Token
                                );
                                response.EnsureSuccessStatusCode();
                                using var stream = await response.Content.ReadAsStreamAsync();
                                var buffer = new byte[102400];
                                int read = 0,
                                    total = 0;
                                while (
                                    (
                                        read = await stream.ReadAsync(
                                            buffer.AsMemory(total, buffer.Length - total),
                                            cts.Token
                                        )
                                    ) > 0
                                    && total < buffer.Length
                                )
                                {
                                    total += read;
                                }

                                return (true, idx);
                            }
                            catch
                            {
                                return (false, idx);
                            }
                        },
                        cts.Token
                    );
                }
            )
            .ToList();

        // Wait for the fastest mirror
        while (tasks.Count > 0)
        {
            var finishedTask = await Task.WhenAny(tasks);
            var (success, mirrorIndex) = finishedTask.Result;
            if (success)
            {
                _fastestMirrorIndex = mirrorIndex;

                // Cancel other unfinished tasks
                for (int i = 0; i < tasks.Count; i++)
                {
                    if (tasks[i] != finishedTask)
                        ctsList[i].Cancel();
                }

                return true;
            }
            else
            {
                int idx = tasks.IndexOf(finishedTask);
                tasks.RemoveAt(idx);
                ctsList.RemoveAt(idx);
            }
        }

        return false;
    }
}
