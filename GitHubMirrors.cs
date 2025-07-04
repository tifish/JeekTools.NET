namespace JeekTools;

public static class GitHubMirrors
{
    public static string[] GetMirrors(string url)
    {
        return
        [
            url,
            url.Replace("https://github.com/", "https://ghfast.top/https://github.com/"),
            url.Replace("https://github.com/", "https://gh-proxy.com/github.com/")
        ];
    }
}