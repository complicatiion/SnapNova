using SnapNova.Core.Media;
using Xunit;

namespace SnapNova.Core.Tests;

public class TrimEditTests
{
    [Fact]
    public void TrimStartAndEnd()
    {
        var edit = new TrimEdit(10);
        edit.SetStart(2);
        edit.SetEnd(8);
        Assert.Equal(new[] { new CutSegment(2, 8) }, edit.Kept);
        Assert.False(edit.IsUncut);
    }

    [Fact]
    public void CutOutTheMiddle()
    {
        var edit = new TrimEdit(10);
        edit.SetSelection(6, 4);
        Assert.Equal(0, edit.CutSelection());
        Assert.Equal(new[] { new CutSegment(0, 4), new CutSegment(6, 10) }, edit.Kept);
        Assert.Equal(8, edit.KeptLength, 3);
        Assert.Equal(6, edit.NextKept(5));
        Assert.False(edit.IsKept(5));
    }

    [Fact]
    public void OverlappingCutsAreJoined()
    {
        var edit = new TrimEdit(10);
        edit.SetSelection(2, 4);
        edit.CutSelection();
        edit.SetSelection(3, 5);
        edit.CutSelection();
        Assert.Single(edit.Removed);
        Assert.Equal(new CutSegment(2, 5), edit.Removed[0]);
    }

    [Fact]
    public void RestoreBringsThePartBack()
    {
        var edit = new TrimEdit(10);
        edit.SetSelection(2, 4);
        edit.CutSelection();
        edit.Restore(0);
        Assert.True(edit.IsUncut);
    }

    [Fact]
    public void EdgeStaysInsideNeighbours()
    {
        var edit = new TrimEdit(10);
        edit.SetSelection(2, 3);
        edit.CutSelection();
        edit.SetSelection(5, 6);
        edit.CutSelection();
        Assert.Equal(5, edit.MoveEdge(0, startEdge: false, 9), 3);
        Assert.Equal(0, edit.MoveEdge(0, startEdge: true, -1), 3);
    }

    [Fact]
    public void TrimDropsCutsOutside()
    {
        var edit = new TrimEdit(10);
        edit.SetSelection(1, 2);
        edit.CutSelection();
        edit.SetStart(3);
        Assert.Empty(edit.Removed);
    }
}
