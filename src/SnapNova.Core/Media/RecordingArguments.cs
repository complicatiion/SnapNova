using System.Globalization;
using SnapNova.Core.Capture;

namespace SnapNova.Core.Media;

/// <summary>Where FFmpeg grabs the screen.</summary>
public abstract record VideoSource;

/// <summary>
/// ddagrab (FFmpeg 6.1+): Desktop Duplication on the GPU. Offsets are relative to the monitor (DXGI output) of the
/// default adapter; frames stay on the GPU for NVENC/AMF.
/// </summary>
public sealed record DdagrabSource(int OutputIndex, int OffsetX, int OffsetY) : VideoSource;

/// <summary>gdigrab: GDI copy in virtual-desktop coordinates. Fallback for Remote Desktop/Citrix sessions and monitors on a second GPU.</summary>
public sealed record GdigrabSource(int X, int Y) : VideoSource;

/// <summary>What the recording is optimised for.</summary>
public enum VideoProfile
{
    /// <summary>Screen content: sharp text and UI, more bits for still parts (default).</summary>
    Screen,

    /// <summary>Movement (games, videos): adaptive quantisation, smoother at the same size.</summary>
    Motion
}

/// <param name="Quality">0 (small files) … 100 (best quality); mapped to CRF/CQ/QP of the encoder.</param>
/// <param name="Chroma444">Full colour resolution (4:4:4) – sharp coloured text; NVENC and the CPU encoders only.</param>
/// <param name="OutputHeight">0 = native; otherwise scaled down to this height (never up).</param>
public sealed record RecordingOptions(
    VideoSource Source,
    int Width,
    int Height,
    int Fps,
    string Encoder,
    int Quality,
    bool DrawMouse,
    string OutputFile,
    VideoProfile Profile = VideoProfile.Screen,
    bool Chroma444 = false,
    int OutputHeight = 0);

/// <summary>FFmpeg command lines for recording, finishing and thumbnails. Pure functions – covered by unit tests.</summary>
public static class RecordingArguments
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Encoders that take ddagrab's D3D11 frames directly (no download to system memory).</summary>
    public static bool AcceptsD3D11(string encoder) => encoder is "h264_nvenc" or "hevc_nvenc" or "h264_amf" or "hevc_amf";

    public static IReadOnlyList<string> Record(RecordingOptions o)
    {
        if (o.Width < 16 || o.Height < 16 || (o.Width & 1) == 1 || (o.Height & 1) == 1)
        {
            throw new ArgumentException("Recording size must be even and at least 16×16 pixels.");
        }

        // -stats with a short period: the recorder reads "drop=" from the progress lines (dropped frames warning).
        var args = new List<string> { "-hide_banner", "-loglevel", "info", "-stats", "-stats_period", "2", "-y" };
        var fps = Math.Clamp(o.Fps, 5, 60).ToString(Inv);
        string? filter;
        var chroma444 = o.Chroma444 && Supports444(o.Encoder);
        var scale = o.OutputHeight > 0 && o.OutputHeight < o.Height
            ? $"scale=-2:{o.OutputHeight}:flags=lanczos,"
            : "";
        var pixelFormat = chroma444 ? "yuv444p" : SoftwarePixelFormat(o.Encoder);

        switch (o.Source)
        {
            case DdagrabSource dda:
                args.AddRange(["-f", "lavfi", "-i",
                    $"ddagrab=output_idx={dda.OutputIndex}:framerate={fps}:video_size={o.Width}x{o.Height}" +
                    $":offset_x={dda.OffsetX}:offset_y={dda.OffsetY}:draw_mouse={(o.DrawMouse ? 1 : 0)}"]);
                // GPU encoders read the D3D11 frames directly; 4:4:4, scaling and the other encoders need system memory.
                filter = AcceptsD3D11(o.Encoder) && !chroma444 && scale.Length == 0
                    ? null
                    : "hwdownload,format=bgra," + scale + "format=" + pixelFormat;
                break;
            case GdigrabSource gdi:
                args.AddRange(["-f", "gdigrab", "-framerate", fps, "-draw_mouse", o.DrawMouse ? "1" : "0",
                    "-offset_x", gdi.X.ToString(Inv), "-offset_y", gdi.Y.ToString(Inv),
                    "-video_size", $"{o.Width}x{o.Height}", "-i", "desktop"]);
                filter = scale + "format=" + pixelFormat;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(o), "Unknown video source.");
        }

        if (filter is not null)
        {
            args.AddRange(["-vf", filter]);
        }

        args.AddRange(["-c:v", o.Encoder]);
        args.AddRange(EncoderQuality(o.Encoder, o.Quality, o.Profile));
        if (chroma444)
        {
            args.AddRange(Profile444(o.Encoder));
        }

        args.AddRange(ContainerFlags(o.OutputFile));
        args.Add(o.OutputFile);
        return args;
    }

    /// <summary>Encoders that can write 4:4:4 (QSV, AMF and Media Foundation fall back to 4:2:0).</summary>
    public static bool Supports444(string encoder) => encoder is "h264_nvenc" or "hevc_nvenc" or "libx264" or "libx265";

    private static IReadOnlyList<string> Profile444(string encoder) => encoder switch
    {
        "h264_nvenc" => ["-profile:v", "high444p"],
        "hevc_nvenc" => ["-profile:v", "rext"],
        "libx264" => ["-profile:v", "high444"],
        _ => []
    };

    /// <summary>
    /// Joins the recorded segments (one per pause) and mixes the audio tracks into the final MP4.
    /// Video is copied, never re-encoded. Audio: AAC 160 kbit/s; two tracks are mixed without halving the volume.
    /// </summary>
    public static IReadOnlyList<string> Finish(string segmentList, IReadOnlyList<string> audioFiles, string output)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", segmentList };
        foreach (var audio in audioFiles)
        {
            args.AddRange(["-i", audio]);
        }

        args.AddRange(["-map", "0:v"]);
        if (audioFiles.Count == 1)
        {
            args.AddRange(["-map", "1:a"]);
        }
        else if (audioFiles.Count > 1)
        {
            var inputs = string.Concat(Enumerable.Range(1, audioFiles.Count).Select(i => $"[{i}:a]"));
            args.AddRange(["-filter_complex", $"{inputs}amix=inputs={audioFiles.Count}:duration=longest:normalize=0[a]", "-map", "[a]"]);
        }

        args.AddRange(["-c:v", "copy"]);
        if (audioFiles.Count > 0)
        {
            // The video is the reference length; audio that runs a few milliseconds longer is cut.
            args.AddRange(["-c:a", "aac", "-b:a", "160k", "-shortest"]);
        }

        args.AddRange(ContainerFlags(output));
        args.Add(output);
        return args;
    }

    /// <summary>MP4: index at the start (plays while loading). MKV/WebM need nothing.</summary>
    public static IReadOnlyList<string> ContainerFlags(string output) =>
        output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? ["-movflags", "+faststart"] : [];

    /// <summary>Concat demuxer list; single quotes in paths are escaped as the demuxer expects.</summary>
    public static string SegmentList(IEnumerable<string> segments) =>
        string.Join('\n', segments.Select(s => "file '" + s.Replace("'", @"'\''") + "'")) + "\n";

    /// <summary>One frame at 1 s (or the first frame of shorter videos), 480 px wide, for the gallery.</summary>
    public static IReadOnlyList<string> Thumbnail(string video, string jpeg) =>
        ["-hide_banner", "-loglevel", "error", "-y", "-ss", "1", "-i", video, "-frames:v", "1", "-vf", "scale=480:-2", jpeg];

    public static IReadOnlyList<string> ThumbnailFirstFrame(string video, string jpeg) =>
        ["-hide_banner", "-loglevel", "error", "-y", "-i", video, "-frames:v", "1", "-vf", "scale=480:-2", jpeg];

    private static string SoftwarePixelFormat(string encoder) => encoder is "h264_qsv" or "h264_mf" or "hevc_qsv" or "hevc_mf" ? "nv12" : "yuv420p";

    /// <summary>The HEVC counterpart of an H.264 encoder (same hardware).</summary>
    public static string ToHevc(string h264Encoder) => h264Encoder switch
    {
        "h264_nvenc" => "hevc_nvenc",
        "h264_amf" => "hevc_amf",
        "h264_qsv" => "hevc_qsv",
        "h264_mf" => "hevc_mf",
        "libx264" => "libx265",
        _ => h264Encoder
    };

    // ---------------------------------------------------------------- video editor and player

    private static string Time(TimeSpan t) => t.TotalSeconds.ToString("0.###", Inv);

    /// <summary>Cut without re-encoding (fast, starts at the nearest keyframe) or exact (re-encode with the given encoder).</summary>
    public static IReadOnlyList<string> Trim(string input, TimeSpan start, TimeSpan end, string output, string? encoder, int quality)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-ss", Time(start), "-to", Time(end), "-i", input };
        if (encoder is null)
        {
            args.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero"]);
        }
        else
        {
            args.AddRange(["-c:v", encoder]);
            args.AddRange(EncoderQuality(encoder, quality));
            args.AddRange(["-pix_fmt", SoftwarePixelFormat(encoder), "-c:a", "aac", "-b:a", "160k"]);
        }

        args.AddRange(ContainerFlags(output));
        args.Add(output);
        return args;
    }

    /// <summary>Animated GIF with its own palette (sharp colours, no dithering noise), looping.</summary>
    public static IReadOnlyList<string> Gif(string input, TimeSpan start, TimeSpan end, int width, int fps, string output) =>
    [
        "-hide_banner", "-loglevel", "error", "-y", "-ss", Time(start), "-to", Time(end), "-i", input,
        "-vf", $"fps={Math.Clamp(fps, 5, 30)},scale={Math.Clamp(width, 120, 1920)}:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5",
        "-loop", "0", output
    ];

    /// <summary>WebM (VP9 + Opus), software encoding – slower, but open formats.</summary>
    public static IReadOnlyList<string> WebM(string input, TimeSpan start, TimeSpan end, int quality, string output) =>
    [
        "-hide_banner", "-loglevel", "error", "-y", "-ss", Time(start), "-to", Time(end), "-i", input,
        "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", (45 - Math.Clamp(quality, 0, 100) * 20 / 100).ToString(Inv), "-row-mt", "1",
        "-deadline", "good", "-cpu-used", "4", "-c:a", "libopus", "-b:a", "128k", output
    ];

    /// <summary>
    /// "Follow the mouse": crops a zoomed window that follows the recorded cursor track (sendcmd file in the working
    /// directory, so no Windows path has to be escaped in the filter) and scales it back to the original size.
    /// </summary>
    public static IReadOnlyList<string> FollowZoom(string input, string commandFile, double zoom, int width, int height,
        TimeSpan start, TimeSpan end, string encoder, int quality, string output)
    {
        var cropW = (int)(width / zoom) & ~1;
        var cropH = (int)(height / zoom) & ~1;
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y", "-ss", Time(start), "-to", Time(end), "-copyts", "-i", input,
            "-vf", $"sendcmd=f={commandFile},crop@z=w={cropW}:h={cropH}:x=0:y=0,scale={width}:{height}:flags=lanczos,setpts=PTS-STARTPTS",
            "-af", "asetpts=PTS-STARTPTS",
            "-c:v", encoder
        };
        args.AddRange(EncoderQuality(encoder, quality));
        args.AddRange(["-pix_fmt", SoftwarePixelFormat(encoder), "-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(ContainerFlags(output));
        args.Add(output);
        return args;
    }

    // ---------------------------------------------------------------- multi-segment cut

    /// <summary>
    /// FFmpeg expression that is true inside the kept parts (absolute times, used with -copyts). It is always placed in
    /// single quotes in the filter graph, which protects the commas.
    /// </summary>
    public static string SelectExpression(IReadOnlyList<CutSegment> keep) =>
        string.Join("+", keep.Select(k => $"between(t,{Time(TimeSpan.FromSeconds(k.Start))},{Time(TimeSpan.FromSeconds(k.End))})"));

    /// <summary>Frame-exact cut of several kept parts in one pass: trim each part, then concat (re-encoded).</summary>
    public static IReadOnlyList<string> CutExact(string input, IReadOnlyList<CutSegment> keep, bool hasAudio, string encoder, int quality, string output)
    {
        var graph = new System.Text.StringBuilder();
        for (var i = 0; i < keep.Count; i++)
        {
            var start = Time(TimeSpan.FromSeconds(keep[i].Start));
            var end = Time(TimeSpan.FromSeconds(keep[i].End));
            graph.Append($"[0:v]trim=start={start}:end={end},setpts=PTS-STARTPTS[v{i}];");
            if (hasAudio)
            {
                graph.Append($"[0:a]atrim=start={start}:end={end},asetpts=PTS-STARTPTS[a{i}];");
            }
        }

        for (var i = 0; i < keep.Count; i++)
        {
            graph.Append(hasAudio ? $"[v{i}][a{i}]" : $"[v{i}]");
        }

        graph.Append($"concat=n={keep.Count}:v=1:a={(hasAudio ? 1 : 0)}[v]{(hasAudio ? "[a]" : "")}");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-filter_complex", graph.ToString(), "-map", "[v]" };
        if (hasAudio)
        {
            args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "160k"]);
        }

        args.AddRange(["-c:v", encoder]);
        args.AddRange(EncoderQuality(encoder, quality));
        args.AddRange(["-pix_fmt", SoftwarePixelFormat(encoder)]);
        args.AddRange(ContainerFlags(output));
        args.Add(output);
        return args;
    }

    /// <summary>
    /// Fast cut without re-encoding: list for the concat demuxer with in/out points per kept part
    /// (cuts land on keyframes; sound and picture may shift slightly at the joins).
    /// </summary>
    public static string CutFastList(string input, IReadOnlyList<CutSegment> keep)
    {
        var text = new System.Text.StringBuilder("ffconcat version 1.0\n");
        foreach (var k in keep)
        {
            text.Append("file '").Append(input.Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
            text.Append("inpoint ").Append(Time(TimeSpan.FromSeconds(k.Start))).Append('\n');
            text.Append("outpoint ").Append(Time(TimeSpan.FromSeconds(k.End))).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Fast cut, step 1: one kept part copied without re-encoding into its own file (start lands on the keyframe before).
    /// Joining these files afterwards (<see cref="ConcatList"/>) is far more robust than in/out points on one input.
    /// </summary>
    public static IReadOnlyList<string> ExtractPart(string input, CutSegment part, string output) =>
    [
        "-hide_banner", "-loglevel", "error", "-y", "-ss", Time(TimeSpan.FromSeconds(part.Start)), "-to", Time(TimeSpan.FromSeconds(part.End)),
        "-i", input, "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", output
    ];

    /// <summary>Fast cut, step 2: concat list of the extracted parts.</summary>
    public static string ConcatList(IEnumerable<string> files)
    {
        var text = new System.Text.StringBuilder("ffconcat version 1.0\n");
        foreach (var file in files)
        {
            text.Append("file '").Append(file.Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
        }

        return text.ToString();
    }

    public static IReadOnlyList<string> CutFast(string listFile, string output)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", "-avoid_negative_ts", "make_zero" };
        args.AddRange(ContainerFlags(output));
        args.Add(output);
        return args;
    }

    /// <summary>GIF of the kept parts (several parts are joined with select).</summary>
    public static IReadOnlyList<string> Gif(string input, IReadOnlyList<CutSegment> keep, int width, int fps, string output) =>
    [
        "-hide_banner", "-loglevel", "error", "-y", "-ss", Time(TimeSpan.FromSeconds(keep[0].Start)), "-copyts", "-i", input,
        "-vf", $"select='{SelectExpression(keep)}',setpts=N/FRAME_RATE/TB,fps={Math.Clamp(fps, 5, 30)},scale={Math.Clamp(width, 120, 1920)}:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5",
        "-loop", "0", output
    ];

    /// <summary>WebM (VP9 + Opus) of the kept parts.</summary>
    public static IReadOnlyList<string> WebM(string input, IReadOnlyList<CutSegment> keep, int quality, string output) =>
    [
        "-hide_banner", "-loglevel", "error", "-y", "-ss", Time(TimeSpan.FromSeconds(keep[0].Start)), "-copyts", "-i", input,
        "-vf", $"select='{SelectExpression(keep)}',setpts=N/FRAME_RATE/TB",
        "-af", $"aselect='{SelectExpression(keep)}',asetpts=N/SR/TB",
        "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", (45 - Math.Clamp(quality, 0, 100) * 20 / 100).ToString(Inv), "-row-mt", "1",
        "-deadline", "good", "-cpu-used", "4", "-c:a", "libopus", "-b:a", "128k", output
    ];

    /// <summary>"Follow the mouse" over the kept parts (crop follows the absolute cursor times, then select).</summary>
    public static IReadOnlyList<string> FollowZoom(string input, string commandFile, double zoom, int width, int height,
        IReadOnlyList<CutSegment> keep, string encoder, int quality, string output)
    {
        var cropW = (int)(width / zoom) & ~1;
        var cropH = (int)(height / zoom) & ~1;
        var select = SelectExpression(keep);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y", "-ss", Time(TimeSpan.FromSeconds(keep[0].Start)), "-copyts", "-i", input,
            "-vf", $"sendcmd=f={commandFile},crop@z=w={cropW}:h={cropH}:x=0:y=0,scale={width}:{height}:flags=lanczos,select='{select}',setpts=N/FRAME_RATE/TB",
            "-af", $"aselect='{select}',asetpts=N/SR/TB",
            "-c:v", encoder
        };
        args.AddRange(EncoderQuality(encoder, quality));
        args.AddRange(["-pix_fmt", SoftwarePixelFormat(encoder), "-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(ContainerFlags(output));
        args.Add(output);
        return args;
    }

    /// <summary>ffprobe: duration, width, height and frame rate as "key=value" lines.</summary>
    public static IReadOnlyList<string> Probe(string input) =>
    [
        "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate:format=duration",
        "-of", "default=noprint_wrappers=1", input
    ];

    /// <summary>Player: raw BGRA frames of the given size from a position.</summary>
    public static IReadOnlyList<string> DecodeVideo(string input, TimeSpan position, int width, int height, int fps) =>
    [
        "-hide_banner", "-loglevel", "error", "-ss", Time(position), "-i", input, "-an",
        "-vf", $"fps={Math.Clamp(fps, 1, 60)},scale={width}:{height}:flags=lanczos", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"
    ];

    /// <summary>Player: 48 kHz stereo 16-bit PCM from a position.</summary>
    public static IReadOnlyList<string> DecodeAudio(string input, TimeSpan position) =>
    [
        "-hide_banner", "-loglevel", "error", "-ss", Time(position), "-i", input, "-vn", "-ac", "2", "-ar", "48000", "-f", "s16le", "pipe:1"
    ];

    /// <summary>Quality 0…100 → constant-quality value of CRF/CQ/QP (lower = better). Screen: 100 → 16, 70 → 20, 0 → 30.</summary>
    public static int QualityValue(int quality, VideoProfile profile = VideoProfile.Screen)
    {
        quality = Math.Clamp(quality, 0, 100);
        return (profile == VideoProfile.Screen ? 30 : 32) - quality * 14 / 100;
    }

    /// <summary>Encoder-specific constant-quality settings. Screen content: no adaptive quantisation (it smears text).</summary>
    internal static IReadOnlyList<string> EncoderQuality(string encoder, int quality, VideoProfile profile = VideoProfile.Screen)
    {
        var q = QualityValue(quality, profile).ToString(Inv);
        var motion = profile == VideoProfile.Motion;
        return encoder switch
        {
            "h264_nvenc" or "hevc_nvenc" => motion
                ? ["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", q, "-b:v", "0", "-spatial-aq", "1", "-temporal-aq", "1"]
                : ["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", q, "-b:v", "0"],
            "h264_amf" => ["-quality", "quality", "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q],
            "hevc_amf" => ["-quality", "quality", "-rc", "cqp", "-qp_i", q, "-qp_p", q],
            "h264_qsv" or "hevc_qsv" => ["-preset", motion ? "medium" : "slower", "-global_quality", q],
            "hevc_mf" or "h264_mf" => ["-rate_control", "quality", "-quality", Math.Clamp(100 - QualityValue(quality, profile) * 2, 40, 100).ToString(Inv)],
            "libx265" => ["-preset", "fast", "-crf", q, "-tag:v", "hvc1"],
            "libx264" => motion ? ["-preset", "veryfast", "-crf", q] : ["-preset", "faster", "-crf", q, "-tune", "animation"],
            _ => []
        };
    }
}
