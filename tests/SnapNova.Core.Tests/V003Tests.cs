using SnapNova.Core.Capture;
using SnapNova.Core.Library;
using SnapNova.Core.Media;
using SnapNova.Core.Ocr;
using SnapNova.Core.Redact;
using SnapNova.Core.Updates;
using Xunit;

namespace SnapNova.Core.Tests;

public class ScrollStitcherTests
{
    /// <summary>A "page" of 400 rows with unique colours; frames are 100-row windows at an offset.</summary>
    private static PixelImage Window(int offset, int width = 8, int height = 100, int header = 0)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var page = y < header ? 10_000 + y : offset + y;
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = (byte)page;
                pixels[i + 1] = (byte)(page >> 8);
                pixels[i + 2] = (byte)(x * 13);
                pixels[i + 3] = 255;
            }
        }

        return new PixelImage(width, height, pixels, stride);
    }

    [Fact]
    public void JoinsScrolledFrames()
    {
        var stitcher = new ScrollStitcher();
        stitcher.Add(Window(0));
        Assert.Equal(30, stitcher.Add(Window(30)));
        Assert.Equal(40, stitcher.Add(Window(70)));
        Assert.Equal(170, stitcher.Result().Height);
    }

    [Fact]
    public void UnchangedFrameAddsNothing()
    {
        var stitcher = new ScrollStitcher();
        stitcher.Add(Window(0));
        Assert.Equal(0, stitcher.Add(Window(0)));
        Assert.Equal(1, stitcher.UnchangedFrames);
    }

    [Fact]
    public void FixedHeaderAppearsOnce()
    {
        var stitcher = new ScrollStitcher();
        stitcher.Add(Window(0, header: 10));
        stitcher.Add(Window(25, header: 10));
        Assert.Equal(125, stitcher.Result().Height);
    }
}

public class RedactDetectorTests
{
    private static OcrResult Ocr(params string[] lines) => new(
        lines.Select((l, i) => new OcrLine(l.Split(' ').Select((w, j) => new OcrWord(w, j * 80, i * 20, 70, 16)).ToList())).ToList(),
        "en");

    [Fact]
    public void FindsEmailAndIp()
    {
        var found = RedactDetector.Find(Ocr("Mail max@example.com from 192.168.10.20"), []);
        Assert.Contains(found, c => c.Kind == RedactKind.Email);
        Assert.Contains(found, c => c.Kind == RedactKind.IpAddress);
    }

    [Fact]
    public void RejectsInvalidIp() =>
        Assert.DoesNotContain(RedactDetector.Find(Ocr("Version 300.1.2.999"), []), c => c.Kind == RedactKind.IpAddress);

    [Fact]
    public void CardNeedsLuhn()
    {
        Assert.Contains(RedactDetector.Find(Ocr("Card 4111 1111 1111 1111"), []), c => c.Kind == RedactKind.CreditCard);
        Assert.DoesNotContain(RedactDetector.Find(Ocr("Card 4111 1111 1111 1112"), []), c => c.Kind == RedactKind.CreditCard);
    }

    [Fact]
    public void IbanNeedsChecksum()
    {
        Assert.Contains(RedactDetector.Find(Ocr("IBAN DE89 3704 0044 0532 0130 00"), []), c => c.Kind == RedactKind.Iban);
        Assert.DoesNotContain(RedactDetector.Find(Ocr("IBAN DE88 3704 0044 0532 0130 00"), []), c => c.Kind == RedactKind.Iban);
    }

    [Fact]
    public void FindsCustomWords() =>
        Assert.Contains(RedactDetector.Find(Ocr("Server ACME-SQL01 down"), ["acme-sql01"]), c => c.Kind == RedactKind.Custom);
}

public class CaptureTrashTests
{
    [Fact]
    public void MoveRestoreAndDelete()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snapnova-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "a.png");
            File.WriteAllBytes(file, [1, 2, 3]);

            var entry = CaptureTrash.MoveToTrash(file);
            Assert.False(File.Exists(file));
            Assert.Single(CaptureTrash.List([folder]));

            Assert.Equal(file, CaptureTrash.Restore(entry));
            Assert.True(File.Exists(file));
            Assert.Empty(CaptureTrash.List([folder]));

            CaptureTrash.DeleteForever(CaptureTrash.MoveToTrash(file));
            Assert.Empty(CaptureTrash.List([folder]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class CursorTrackTests
{
    [Fact]
    public void CropCommandsStayInsideFrame()
    {
        var track = new CursorTrack { Width = 1920, Height = 1080, Samples = [new(0, 0, 0, false), new(1, 1919, 1079, false)] };
        var text = track.ToCropCommands(2, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Contains("crop@z x", text);
        foreach (var number in System.Text.RegularExpressions.Regex.Matches(text, @"crop@z x (\d+)").Select(m => int.Parse(m.Groups[1].Value)))
        {
            Assert.InRange(number, 0, 1920 - 960);
        }
    }
}

public class V003ArgumentTests
{
    [Fact]
    public void TrimCopyUsesStreamCopy()
    {
        var args = RecordingArguments.Trim("in.mkv", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), "out.mp4", null, 70);
        Assert.Contains("copy", args);
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void GifUsesPalette() =>
        Assert.Contains(RecordingArguments.Gif("in.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(3), 640, 15, "out.gif"), a => a.Contains("palettegen"));

    [Fact]
    public void HevcMapping() => Assert.Equal("hevc_nvenc", RecordingArguments.ToHevc("h264_nvenc"));

    [Fact]
    public void FollowZoomUsesSendcmd() =>
        Assert.Contains(RecordingArguments.FollowZoom("in.mp4", "follow.cmd", 2, 1920, 1080, TimeSpan.Zero, TimeSpan.FromSeconds(2), "libx264", 70, "out.mp4"),
            a => a.StartsWith("sendcmd=f=follow.cmd", StringComparison.Ordinal));
}

public class ReleaseCheckerTests
{
    [Theory]
    [InlineData("0.0.4", "0.0.3", true)]
    [InlineData("v0.1.0", "0.0.9", true)]
    [InlineData("0.0.3", "0.0.3", false)]
    [InlineData("0.0.2", "0.0.3", false)]
    public void ComparesVersions(string candidate, string current, bool newer) =>
        Assert.Equal(newer, ReleaseChecker.IsNewer(candidate, current));
}
