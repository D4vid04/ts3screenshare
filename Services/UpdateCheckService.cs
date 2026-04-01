using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading.Tasks;

namespace TS3ScreenShare.Services;

public sealed record UpdateInfo(string LatestVersion, string ReleaseUrl);

public sealed class UpdateCheckService
{
    private const string ApiUrl =
        "https://api.github.com/repos/D4vid04/ts3screenshare/releases/latest";

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Returns UpdateInfo if a newer release exists on GitHub, otherwise null.
    /// Never throws — silently returns null on any failure.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TS3ScreenShare");
            http.Timeout = TimeSpan.FromSeconds(10);

            var release = await http.GetFromJsonAsync<GitHubRelease>(ApiUrl);
            if (release is null) return null;

            var latestRaw = release.TagName.TrimStart('v');
            if (!Version.TryParse(latestRaw, out var latest)) return null;
            if (!Version.TryParse(CurrentVersion, out var current)) return null;

            return latest > current
                ? new UpdateInfo(latestRaw, release.HtmlUrl)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class GitHubRelease
    {
        [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        public string TagName { get; init; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = "";
    }
}
