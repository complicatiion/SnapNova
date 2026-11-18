using SnapNova.Core.Annotations;
using SnapNova.Core.Capture;
using SnapNova.Core.Library;
using SnapNova.Core.Media;
using Xunit;

namespace SnapNova.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+S")]
    [InlineData("Alt+F9")]
    [InlineData("Ctrl+Alt+Shift+Win+PrintScreen")]
    [InlineData("Ctrl+Num5")]
    public void Roundtrip(string text) => Assert.Equal(text, Hotkey.Parse(text).ToString());

    [Fact]
    public void ParsesCaseInsensitive()
    {
        var key = Hotkey.Parse("ctrl + shift + s");
        Assert.True(key.Ctrl);
        Assert.True(key.Shift);
        Assert.Equal((uint)'S', key.VirtualKey);
    }

    [Fact]
    public void EmptyAndInvalid()
    {
        Assert.True(Hotkey.Parse("").IsEmpty);
        Assert.True(Hotkey.Parse("Ctrl+Shift").IsEmpty);
        Assert.Equal("", Hotkey.Parse(null).ToString());
    }

    [Fact]
    public void ModifiersIncludeNoRepeat() =>
        Assert.Equal(Hotkey.ModControl | Hotkey.ModShift | Hotkey.ModNoRepeat, Hotkey.Parse("Ctrl+Shift+R").Modifiers);
}

public class ScreenRectTests
{
    [Fact]
    public void FromPointsAnyDirection() => Assert.Equal(new ScreenRect(10, 20, 30, 40), ScreenRect.FromPoints(40, 60, 10, 20));

    [Fact]
    public void EvenSize() => Assert.Equal(new ScreenRect(1, 1, 100, 50), new ScreenRect(1, 1, 101, 51).EvenSize());

    [Fact]
    public void Intersect()
    {
        Assert.Equal(new ScreenRect(5, 5, 5, 5), new ScreenRect(0, 0, 10, 10).Intersect(new ScreenRect(5, 5, 10, 10)));
        Assert.True(new ScreenRect(0, 0, 10, 10).Intersect(new ScreenRect(20, 20, 5, 5)).IsEmpty);
    }

    [Fact]
    public void CropCopiesRows()
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)i;
        }

        var image = new PixelImage(4, 4, pixels, 16);
        var crop = image.Crop(new ScreenRect(1, 2, 2, 2));
        Assert.Equal(2, crop.Width);
        Assert.Equal(pixels[2 * 16 + 4], crop.Pixels[0]);
        Assert.Equal(pixels[3 * 16 + 4], crop.Pixels[8]);
    }
}

public class AnnotationTests
{
    [Fact]
    public void JsonRoundtripKeepsShapeTypes()
    {
        var doc = new AnnotationDocument
        {
            Crop = new ScreenRect(1, 2, 300, 200),
            Info = new CaptureInfo(DateTimeOffset.Now, "notepad", "Untitled", new ScreenRect(0, 0, 800, 600)),
            Shapes =
            {
                new ArrowShape { X1 = 1, Y1 = 2, X2 = 30, Y2 = 40, Color = 0xFFE53935 },
                new RectangleShape { X = 5, Y = 5, Width = 20, Height = 10, Filled = true },
                new TextShape { X = 3, Y = 4, Text = "Hallo\nWelt" },
                new StepShape { X = 9, Y = 9, Number = 3 },
                new PixelateShape { X = 0, Y = 0, Width = 50, Height = 50, BlockSize = 16 }
            }
        };

        var copy = AnnotationDocument.FromJson(doc.ToJson());
        Assert.Equal(doc.Crop, copy.Crop);
        Assert.Equal("notepad", copy.Info?.SourceApp);
        Assert.Collection(copy.Shapes,
            s => Assert.Equal(0xFFE53935, Assert.IsType<ArrowShape>(s).Color),
            s => Assert.True(Assert.IsType<RectangleShape>(s).Filled),
            s => Assert.Equal("Hallo\nWelt", Assert.IsType<TextShape>(s).Text),
            s => Assert.Equal(3, Assert.IsType<StepShape>(s).Number),
            s => Assert.Equal(16, Assert.IsType<PixelateShape>(s).BlockSize));
    }

    [Fact]
    public void LineHitTestUsesDistance()
    {
        var line = new LineShape { X1 = 0, Y1 = 0, X2 = 100, Y2 = 0, StrokeWidth = 2 };
        Assert.True(line.HitTest(50, 3, 2));
        Assert.False(line.HitTest(50, 30, 2));
    }

    [Fact]
    public void SnovaFileRoundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snapnova-test-{Guid.NewGuid():N}.snova");
        try
        {
            var png = new byte[] { 137, 80, 78, 71, 1, 2, 3 };
            SnovaFile.Save(path, png, new AnnotationDocument { Shapes = { new LineShape() } }, [9, 9]);
            var (original, document) = SnovaFile.Load(path);
            Assert.Equal(png, original);
            Assert.Single(document.Shapes);
            Assert.Equal(new byte[] { 9, 9 }, SnovaFile.ReadThumbnail(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class LibraryTests
{
    [Fact]
    public void ExportNextToProjectIsHidden()
    {
        var dir = Directory.CreateTempSubdirectory("snapnova-lib-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.snova"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "a.png"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "b.png"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "c.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "notes.txt"), [1]);

            var names = CaptureLibrary.Scan(dir).Select(i => Path.GetFileName(i.Path)).Order().ToList();
            Assert.Equal(new[] { "a.snova", "b.png", "c.mp4" }, names);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NewFileNameIsUnique()
    {
        var dir = Directory.CreateTempSubdirectory("snapnova-lib-").FullName;
        try
        {
            var time = new DateTime(2026, 11, 14, 20, 15, 3);
            var first = CaptureLibrary.NewFileName(dir, ".snova", time);
            Assert.EndsWith("SnapNova_2026-11-14_20-15-03.snova", first);
            File.WriteAllBytes(first, [1]);
            Assert.EndsWith("SnapNova_2026-11-14_20-15-03_2.snova", CaptureLibrary.NewFileName(dir, ".snova", time));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class RecordingArgumentTests
{
    [Fact]
    public void DdagrabWithNvencKeepsFramesOnGpu()
    {
        var args = RecordingArguments.Record(new RecordingOptions(new DdagrabSource(1, 100, 50), 1280, 720, 30, "h264_nvenc", 70, true, "out.mp4"));
        var joined = string.Join(' ', args);
        Assert.Contains("ddagrab=output_idx=1:framerate=30:video_size=1280x720:offset_x=100:offset_y=50:draw_mouse=1", joined);
        Assert.DoesNotContain("hwdownload", joined);
        Assert.Contains("-c:v h264_nvenc", joined);
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void DdagrabWithMediaFoundationDownloadsFrames()
    {
        var joined = string.Join(' ', RecordingArguments.Record(new RecordingOptions(new DdagrabSource(0, 0, 0), 640, 480, 30, "h264_mf", 70, false, "o.mp4")));
        Assert.Contains("hwdownload,format=bgra,format=nv12", joined);
        Assert.Contains("draw_mouse=0", joined);
    }

    [Fact]
    public void GdigrabUsesDesktopCoordinates()
    {
        var joined = string.Join(' ', RecordingArguments.Record(new RecordingOptions(new GdigrabSource(-1920, 0), 1920, 1080, 60, "libx264", 100, true, "o.mp4")));
        Assert.Contains("-f gdigrab -framerate 60", joined);
        Assert.Contains("-offset_x -1920 -offset_y 0", joined);
        Assert.Contains("-crf 16", joined);
    }

    [Fact]
    public void OddSizeIsRejected() =>
        Assert.Throws<ArgumentException>(() => RecordingArguments.Record(new RecordingOptions(new GdigrabSource(0, 0), 101, 100, 30, "h264_mf", 70, true, "o.mp4")));

    [Fact]
    public void FinishMixesTwoTracks()
    {
        var joined = string.Join(' ', RecordingArguments.Finish("list.txt", ["system.wav", "microphone.wav"], "final.mp4"));
        Assert.Contains("[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[a]", joined);
        Assert.Contains("-c:v copy", joined);
        Assert.Contains("-c:a aac", joined);
    }

    [Fact]
    public void FinishWithoutAudioOnlyCopiesVideo()
    {
        var joined = string.Join(' ', RecordingArguments.Finish("list.txt", [], "final.mp4"));
        Assert.DoesNotContain("-c:a", joined);
        Assert.DoesNotContain("amix", joined);
    }

    [Fact]
    public void SegmentListEscapesQuotes() =>
        Assert.Equal("file 'C:\\a'\\''b\\s1.mp4'\n", RecordingArguments.SegmentList(["C:\\a'b\\s1.mp4"]));

    [Fact]
    public void AssetNamesFollowBtbNScheme()
    {
        Assert.Equal("ffmpeg-n8.1-latest-win64-lgpl-8.1.zip", FfmpegProvisioner.AssetName(FfmpegVariant.Lgpl));
        Assert.Equal("ffmpeg-n8.1-latest-win64-gpl-8.1.zip", FfmpegProvisioner.AssetName(FfmpegVariant.Gpl));
    }

    [Fact]
    public void CaptureModeParse()
    {
        Assert.Equal(CaptureMode.ImageRegion, CaptureModeExtensions.Parse("region"));
        Assert.Equal(CaptureMode.VideoFullScreen, CaptureModeExtensions.Parse("VIDEO-FULLSCREEN"));
        Assert.Equal(CaptureMode.ImageScrolling, CaptureModeExtensions.Parse("scrolling"));
        Assert.Null(CaptureModeExtensions.Parse("panorama"));
    }
}
