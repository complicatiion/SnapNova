using SnapNova.Core.Media;
using Xunit;

namespace SnapNova.Core.Tests;

public class RecordingQualityTests
{
    [Theory]
    [InlineData(100, 16)]
    [InlineData(70, 21)]
    [InlineData(0, 30)]
    public void ScreenQualityMapping(int quality, int expected) =>
        Assert.Equal(expected, RecordingArguments.QualityValue(quality));

    [Fact]
    public void Chroma444DownloadsFramesForNvenc()
    {
        var joined = string.Join(' ', RecordingArguments.Record(new RecordingOptions(new DdagrabSource(0, 0, 0), 1920, 1080, 30, "h264_nvenc", 70,
            false, "o.mkv", Chroma444: true)));
        Assert.Contains("hwdownload,format=bgra,format=yuv444p", joined);
        Assert.Contains("-profile:v high444p", joined);
    }

    [Fact]
    public void Chroma444IgnoredForQsv()
    {
        var joined = string.Join(' ', RecordingArguments.Record(new RecordingOptions(new DdagrabSource(0, 0, 0), 1920, 1080, 30, "h264_qsv", 70,
            false, "o.mkv", Chroma444: true)));
        Assert.Contains("format=nv12", joined);
        Assert.DoesNotContain("444", joined);
    }

    [Fact]
    public void ScalesDownButNeverUp()
    {
        var down = string.Join(' ', RecordingArguments.Record(new RecordingOptions(new GdigrabSource(0, 0), 2560, 1440, 30, "libx264", 70, false, "o.mp4",
            OutputHeight: 1080)));
        Assert.Contains("scale=-2:1080:flags=lanczos", down);
        var up = string.Join(' ', RecordingArguments.Record(new RecordingOptions(new GdigrabSource(0, 0), 1280, 720, 30, "libx264", 70, false, "o.mp4",
            OutputHeight: 1080)));
        Assert.DoesNotContain("scale=", up);
    }

    [Fact]
    public void NvencDirectPathWithoutScalingOr444()
    {
        var joined = string.Join(' ', RecordingArguments.Record(new RecordingOptions(new DdagrabSource(0, 0, 0), 1920, 1080, 30, "h264_nvenc", 70, false, "o.mkv")));
        Assert.DoesNotContain("hwdownload", joined);
        Assert.Contains("-stats_period 2", joined);
    }

    [Fact]
    public void MultiRecordingPartsShareTheMainName()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snapnova-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var main = Path.Combine(folder, "SnapNova_2026-11-18_10-00-00.mp4");
            var part = SnapNova.Core.Library.CaptureLibrary.PartFileName(folder, ".mp4", main, 2);
            Assert.Equal("SnapNova_2026-11-18_10-00-00_rec2.mp4", Path.GetFileName(part));
            File.WriteAllText(part, "");
            var again = SnapNova.Core.Library.CaptureLibrary.PartFileName(folder, ".mp4", main, 2);
            Assert.Equal("SnapNova_2026-11-18_10-00-00_rec2-2.mp4", Path.GetFileName(again));
            Assert.Equal(("SnapNova_2026-11-18_10-00-00", 2), SnapNova.Core.Library.CaptureLibrary.PartOf("SnapNova_2026-11-18_10-00-00_rec2-2"));
            Assert.Equal(("SnapNova_2026-11-18_10-00-00", 3), SnapNova.Core.Library.CaptureLibrary.PartOf("SnapNova_2026-11-18_10-00-00_rec3"));
            Assert.Null(SnapNova.Core.Library.CaptureLibrary.PartOf("SnapNova_2026-11-18_10-00-00_2"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
