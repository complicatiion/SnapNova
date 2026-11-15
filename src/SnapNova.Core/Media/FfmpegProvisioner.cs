using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapNova.Core.Media;

/// <summary>
/// FFmpeg opt-in: SnapNova never ships FFmpeg. After the user accepted the license notice, the official Windows build
/// is downloaded from BtbN/FFmpeg-Builds (GitHub), verified against the checksums.sha256 of the same release and
/// unpacked to %LocalAppData%\SnapNova\tools\ffmpeg. FFmpeg then only ever runs as a separate process (ffmpeg.exe).
/// Alternatively the user picks an existing ffmpeg.exe (offline PCs, company proxies, own builds).
/// </summary>
public sealed class FfmpegProvisioner
{
    /// <summary>Raise when the notice text changes – users have to accept the new text again.</summary>
    public const int NoticeVersion = 1;

    // "latest" floats and always points to the newest build of the branch; old daily builds are deleted after 14 days.
    private const string ReleaseBase = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";
    private const string Branch = "8.1";
    private const long MaxDownloadBytes = 400L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly string _root;
    private readonly HttpClient _http;

    public FfmpegProvisioner(string dataRoot, HttpClient http)
    {
        _root = Path.Combine(dataRoot, "tools", "ffmpeg");
        _http = http;
    }

    private string StateFile => Path.Combine(_root, "ffmpeg.json");

    /// <summary>LGPL build by default (no GPL-only libraries such as x264); GPL is an option in the settings.</summary>
    public static string AssetName(FfmpegVariant variant) =>
        $"ffmpeg-n{Branch}-latest-win64-{(variant == FfmpegVariant.Gpl ? "gpl" : "lgpl")}-{Branch}.zip";

    public FfmpegState? LoadState()
    {
        try
        {
            return File.Exists(StateFile) ? JsonSerializer.Deserialize<FfmpegState>(File.ReadAllText(StateFile), Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Path of a usable ffmpeg.exe, or null (not consented, not installed, file gone).</summary>
    public string? ResolveExecutable()
    {
        var state = LoadState();
        return state is { NoticeAccepted: NoticeVersion } && File.Exists(state.Executable) ? state.Executable : null;
    }

    /// <summary>Records the consent (date, notice version, variant) – before anything is downloaded.</summary>
    public void AcceptNotice(FfmpegVariant variant)
    {
        var state = (LoadState() ?? new FfmpegState()) with
        {
            NoticeAccepted = NoticeVersion,
            NoticeAcceptedAt = DateTimeOffset.Now,
            Variant = variant
        };
        Save(state);
    }

    /// <summary>Downloads, verifies and unpacks FFmpeg. Requires <see cref="AcceptNotice"/> first.</summary>
    public async Task<FfmpegState> InstallAsync(IProgress<FfmpegProgress>? progress, CancellationToken ct)
    {
        var state = LoadState();
        if (state is not { NoticeAccepted: NoticeVersion })
        {
            throw new InvalidOperationException("The FFmpeg license notice has not been accepted.");
        }

        var asset = AssetName(state.Variant);
        Directory.CreateDirectory(_root);

        // 1. Expected hash from the release itself (small file, fetched first).
        progress?.Report(new FfmpegProgress(FfmpegStep.Checksums, 0, null));
        var expected = await ExpectedHashAsync(asset, ct);

        // 2. Download to a .part file with progress; hash while writing (no second pass over ~140 MB).
        var zip = Path.Combine(_root, asset + ".part");
        string actual;
        try
        {
            actual = await DownloadAsync(ReleaseBase + asset, zip, progress, ct);
        }
        catch
        {
            TryDelete(zip);
            throw;
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(zip);
            // "latest" may have been replaced between the two requests – a retry fetches matching files.
            throw new FfmpegIntegrityException(asset, expected, actual);
        }

        // 3. Unpack only what is needed (ffmpeg.exe, ffprobe.exe, LICENSE.txt) to a fresh folder, then switch over.
        progress?.Report(new FfmpegProgress(FfmpegStep.Extracting, 0, null));
        var target = Path.Combine(_root, "build-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        try
        {
            ExtractSelected(zip, target);
        }
        catch
        {
            TryDeleteDirectory(target);
            throw;
        }
        finally
        {
            TryDelete(zip);
        }

        // 4. Validate by running it.
        progress?.Report(new FfmpegProgress(FfmpegStep.Validating, 0, null));
        var exe = Path.Combine(target, "ffmpeg.exe");
        var version = await FfmpegProbe.VersionAsync(exe, ct)
                      ?? throw new InvalidOperationException("The downloaded ffmpeg.exe does not start.");

        var previous = state.Executable;
        state = state with
        {
            Executable = exe,
            Version = version,
            Source = ReleaseBase + asset,
            Sha256 = actual.ToLowerInvariant(),
            InstalledAt = DateTimeOffset.Now,
            Custom = false
        };
        Save(state);

        // Old managed build (update): remove it; never touch a user-chosen ffmpeg.exe.
        if (previous is not null && previous.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteDirectory(Path.GetDirectoryName(previous)!);
        }

        progress?.Report(new FfmpegProgress(FfmpegStep.Done, 1, null));
        return state;
    }

    /// <summary>Uses an existing ffmpeg.exe (no download). The notice still has to be accepted.</summary>
    public async Task<FfmpegState> UseExistingAsync(string executable, CancellationToken ct)
    {
        var state = LoadState();
        if (state is not { NoticeAccepted: NoticeVersion })
        {
            throw new InvalidOperationException("The FFmpeg license notice has not been accepted.");
        }

        var version = await FfmpegProbe.VersionAsync(executable, ct)
                      ?? throw new InvalidOperationException("The selected file is not a working ffmpeg.exe.");
        state = state with { Executable = executable, Version = version, Source = null, Sha256 = null, InstalledAt = DateTimeOffset.Now, Custom = true };
        Save(state);
        return state;
    }

    /// <summary>Settings › Video › Remove FFmpeg: deletes the managed copy and the consent.</summary>
    public void Remove()
    {
        TryDeleteDirectory(_root);
    }

    private async Task<string> ExpectedHashAsync(string asset, CancellationToken ct)
    {
        var text = await _http.GetStringAsync(ReleaseBase + "checksums.sha256", ct);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Format: "<sha256>  <file name>" (sha256sum; file name may carry a leading '*' or "./")
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length == 64
                && string.Equals(parts[1].TrimStart('*').Replace("./", ""), asset, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        throw new InvalidOperationException($"{asset} is not listed in checksums.sha256 of the release.");
    }

    private async Task<string> DownloadAsync(string url, string file, IProgress<FfmpegProgress>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        if (total > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"Unexpected download size ({total} bytes).");
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long done = 0;
        var lastReport = Stopwatch.StartNew();
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            sha.AppendData(buffer, 0, read);
            done += read;
            if (done > MaxDownloadBytes)
            {
                throw new InvalidOperationException("Download exceeds the size limit.");
            }

            if (lastReport.ElapsedMilliseconds > 100)
            {
                progress?.Report(new FfmpegProgress(FfmpegStep.Downloading, total is > 0 ? (double)done / total.Value : 0, done));
                lastReport.Restart();
            }
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void ExtractSelected(string zip, string target)
    {
        var wanted = new[] { "ffmpeg.exe", "ffprobe.exe", "LICENSE.txt" };
        Directory.CreateDirectory(target);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            // Only bare file names are used – no paths from the archive (no zip slip).
            var name = Path.GetFileName(entry.FullName);
            if (wanted.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                entry.ExtractToFile(Path.Combine(target, name), overwrite: true);
            }
        }

        if (!File.Exists(Path.Combine(target, "ffmpeg.exe")))
        {
            throw new InvalidOperationException("ffmpeg.exe not found in the archive.");
        }
    }

    private void Save(FfmpegState state)
    {
        Directory.CreateDirectory(_root);
        var temp = StateFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, StateFile, overwrite: true);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public enum FfmpegVariant
{
    Lgpl,
    Gpl
}

public enum FfmpegStep
{
    Checksums,
    Downloading,
    Extracting,
    Validating,
    Done
}

/// <param name="Fraction">0..1 (0 when the size is unknown).</param>
public readonly record struct FfmpegProgress(FfmpegStep Step, double Fraction, long? BytesDone);

/// <summary>%LocalAppData%\SnapNova\tools\ffmpeg\ffmpeg.json – consent and installed build (shown in Settings › Video).</summary>
public sealed record FfmpegState
{
    public int NoticeAccepted { get; init; }
    public DateTimeOffset? NoticeAcceptedAt { get; init; }
    public FfmpegVariant Variant { get; init; }
    public string? Executable { get; init; }
    public string? Version { get; init; }
    public string? Source { get; init; }
    public string? Sha256 { get; init; }
    public DateTimeOffset? InstalledAt { get; init; }
    public bool Custom { get; init; }
}

public sealed class FfmpegIntegrityException(string asset, string expected, string actual)
    : Exception($"SHA-256 of {asset} does not match the release checksum (expected {expected}, got {actual}).");
