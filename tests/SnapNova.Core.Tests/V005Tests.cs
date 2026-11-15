using SnapNova.Core.Annotations;
using SnapNova.Core.Media;
using Xunit;

namespace SnapNova.Core.Tests;

public class CutArgumentTests
{
    private static readonly CutSegment[] Keep = [new(0, 3), new(7, 10)];

    [Fact]
    public void SelectExpressionCoversAllParts() =>
        Assert.Equal("between(t,0,3)+between(t,7,10)", RecordingArguments.SelectExpression(Keep));

    [Fact]
    public void ExactCutConcatsAllParts()
    {
        var args = RecordingArguments.CutExact("in.mkv", Keep, hasAudio: true, "libx264", 70, "out.mp4");
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("concat=n=2:v=1:a=1", graph);
        Assert.Contains("atrim=start=7:end=10", graph);
    }

    [Fact]
    public void ExactCutWithoutAudio()
    {
        var args = RecordingArguments.CutExact("in.mkv", Keep, hasAudio: false, "libx264", 70, "out.mp4");
        Assert.DoesNotContain(args, a => a.Contains("atrim", StringComparison.Ordinal));
        Assert.Contains(args, a => a.Contains("concat=n=2:v=1:a=0", StringComparison.Ordinal));
    }

    [Fact]
    public void FastListHasInAndOutPoints()
    {
        var text = RecordingArguments.CutFastList(@"C:\Videos\a'b.mp4", Keep);
        Assert.Contains("file 'C:/Videos/a'\\''b.mp4'", text);
        Assert.Contains("inpoint 7", text);
        Assert.Contains("outpoint 10", text);
    }
}

public class EditableShapeTests
{
    [Fact]
    public void StrokeBoundsAndHit()
    {
        var stroke = new StrokeShape { Points = [10, 10, 50, 10, 50, 40], StrokeWidth = 4 };
        Assert.Equal(new ShapeBounds(10, 10, 40, 30), stroke.GetBounds());
        Assert.True(stroke.HitTest(30, 11, 2));
        Assert.False(stroke.HitTest(30, 30, 2));
    }

    [Fact]
    public void StrokeScalesIntoNewBounds()
    {
        var stroke = new StrokeShape { Points = [0, 0, 10, 10] };
        stroke.SetBounds(new ShapeBounds(100, 100, 20, 20));
        Assert.Equal(new[] { 100f, 100f, 120f, 120f }, stroke.Points);
    }

    [Fact]
    public void RotatedRectangleHitTest()
    {
        // 100 × 10 bar, rotated 90°: now vertical around its centre (50, 5).
        var rect = new RectangleShape { X = 0, Y = 0, Width = 100, Height = 10, StrokeWidth = 2, Rotation = 90 };
        Assert.True(rect.HitTest(50, 40, 1));
        Assert.False(rect.HitTest(90, 5, 1));
    }

    [Fact]
    public void RotationIsSavedAndLoaded()
    {
        var doc = new AnnotationDocument { Shapes = [new EllipseShape { X = 1, Y = 2, Width = 3, Height = 4, Rotation = 30 }] };
        var copy = AnnotationDocument.FromJson(doc.ToJson());
        Assert.Equal(30, copy.Shapes[0].Rotation);
    }
}
