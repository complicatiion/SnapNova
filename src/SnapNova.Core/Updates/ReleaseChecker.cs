using System.Net.Http.Headers;
using System.Text.Json;

namespace SnapNova.Core.Updates;

/// <summary>A file of a GitHub release (setup, portable ZIP, .sha256).</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

public sealed record ReleaseInfo(string Version, string Url, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// Opt-in check for a newer SnapNova release on GitHub (github.com/complicatiion/SnapNova). Only the request itself is
/// sent; downloads happen only when the user clicks Download.
/// </summary>
public static class ReleaseChecker
{
    public const string Repository = "complicatiion/SnapNova";
    public const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    /// <summary>Only files of SnapNova's own releases are accepted.</summary>
    public const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

    public static async Task<ReleaseInfo?> GetLatestAsync(HttpClient http, string currentVersion, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SnapNova", currentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : null;
    }

    internal static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        if (tag is null || url is null || !url.StartsWith("https://github.com/" + Repository + "/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var download = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                if (name is not null && download is not null && download.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    assets.Add(new ReleaseAsset(name, download, size));
                }
            }
        }

        return new ReleaseInfo(tag.TrimStart('v', 'V'), url, assets);
    }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate.TrimStart('v', 'V').Split('-', '+')[0], out var a)
        && Version.TryParse(current.TrimStart('v', 'V').Split('-', '+')[0], out var b) && a > b;
}
