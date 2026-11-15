using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SnapNova.Core.Capture;

/// <summary>One cursor position during a recording, relative to the recorded area (pixels), time from the video start.</summary>
public readonly record struct CursorSample(double Time, int X, int Y, bool Down);

/// <summary>
/// The mouse track of a recording (saved next to the video as &lt;name&gt;.cursor.json). Used by the video editor for
/// "Follow the mouse": a zoomed window that follows the smoothed track.
/// </summary>
public sealed class CursorTrack
{
    public int Width { get; set; }

    public int Height { get; set; }

    public List<CursorSample> Samples { get; set; } = [];

    public static string PathFor(string video) => Path.ChangeExtension(video, ".cursor.json");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));

    public static CursorTrack? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<CursorTrack>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// sendcmd script for FFmpeg's crop filter: the crop window (size width/zoom × height/zoom) follows the cursor,
    /// smoothed (exponential, so the picture glides instead of jumping) and clamped to the frame, 15 updates per second.
    /// </summary>
    public string ToCropCommands(double zoom, TimeSpan start, TimeSpan end)
    {
        var cropW = (int)(Width / zoom) & ~1;
        var cropH = (int)(Height / zoom) & ~1;
        var text = new StringBuilder();
        if (Samples.Count == 0)
        {
            return text.ToString();
        }

        double sx = Samples[0].X, sy = Samples[0].Y;
        var index = 0;
        for (var t = 0.0; t <= end.TotalSeconds + 0.001; t += 1.0 / 15)
        {
            while (index < Samples.Count - 1 && Samples[index + 1].Time <= t)
            {
                index++;
            }

            var target = Samples[index];
            sx += (target.X - sx) * 0.25;
            sy += (target.Y - sy) * 0.25;
            if (t < start.TotalSeconds - 0.001)
            {
                continue;
            }

            var x = Math.Clamp((int)(sx - cropW / 2.0), 0, Math.Max(0, Width - cropW));
            var y = Math.Clamp((int)(sy - cropH / 2.0), 0, Math.Max(0, Height - cropH));
            text.Append(t.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" crop@z x ").Append(x).Append(", crop@z y ").Append(y).Append(";\n");
        }

        return text.ToString();
    }
}
