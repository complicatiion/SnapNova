using System.Security.Cryptography;
using SnapNova.App.Localization;
using SnapNova.Core.Updates;

namespace SnapNova.App.Services;

/// <summary>
/// Opt-in update check against GitHub Releases (complicatiion/SnapNova). Off by default; switching it on asks once.
/// Automatic: at most once a day. Download only on click, SHA-256 checked against the .sha256 of the release;
/// installing is always started by the user.
/// </summary>
public sealed class UpdateService(UiSettings settings, HttpClient http, AppHost host)
{
    public ReleaseInfo? Latest { get; private set; }

    public string StatusText { get; private set; } = "";

    public event Action? StateChanged;

    /// <summary>Background check at start-up (only when switched on and the last check is older than a day).</summary>
    public async Task CheckOnStartAsync()
    {
        if (settings.UpdateCheck && settings.UpdateConsentAsked && (settings.LastUpdateCheck is null || DateTimeOffset.Now - settings.LastUpdateCheck > TimeSpan.FromDays(1)))
        {
            await CheckAsync(manual: false);
        }
    }

    public async Task CheckAsync(bool manual)
    {
        if (!settings.UpdateCheck && !manual)
        {
            return;
        }

        SetStatus(Loc.T("UpdChecking"));
        try
        {
            var latest = await ReleaseChecker.GetLatestAsync(http, AppEnvironment.Version, CancellationToken.None);
            settings.LastUpdateCheck = DateTimeOffset.Now;
            settings.Save();
            Latest = latest is not null && ReleaseChecker.IsNewer(latest.Version, AppEnvironment.Version) ? latest : null;
            SetStatus(Latest is null ? Loc.F("UpdCurrent", AppEnvironment.Version) : Loc.F("UpdAvailable", Latest.Version));
            if (Latest is not null && !manual)
            {
                host.ShowToast(Loc.F("UpdAvailable", Latest.Version));
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            SetStatus(Loc.F("UpdFailed", e.Message));
        }
    }

    /// <summary>Setup (installed) or portable ZIP (portable copy) of the newest release, checked with its .sha256.</summary>
    public async Task DownloadAsync()
    {
        if (Latest is not { } release)
        {
            return;
        }

        var suffix = AppEnvironment.IsPortable ? "_portable.zip" : "_setup.exe";
        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            AppEnvironment.OpenUrl(release.Url);
            return;
        }

        try
        {
            SetStatus(Loc.T("UpdDownloading"));
            var folder = Path.Combine(AppEnvironment.DataDirectory, "updates");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, asset.Name);
            await using (var input = await http.GetStreamAsync(asset.Url))
            await using (var output = File.Create(target))
            {
                await input.CopyToAsync(output);
            }

            var checksum = release.Assets.FirstOrDefault(a => a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
            if (checksum is not null)
            {
                var expected = (await http.GetStringAsync(checksum.Url)).Split(' ', 2)[0].Trim();
                await using var file = File.OpenRead(target);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    file.Close();
                    File.Delete(target);
                    SetStatus(Loc.T("UpdChecksumFailed"));
                    return;
                }
            }

            SetStatus(Loc.F("UpdDownloaded", asset.Name));
            AppEnvironment.OpenFolder(target);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            SetStatus(Loc.F("UpdFailed", e.Message));
        }
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        StateChanged?.Invoke();
    }
}
