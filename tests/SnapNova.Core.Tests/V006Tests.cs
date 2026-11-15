using SnapNova.Core.Annotations;
using SnapNova.Core.Media;
using Xunit;

namespace SnapNova.Core.Tests;

public class TextPlateTests
{
    [Fact]
    public void OldTextsKeepTheirLabel()
    {
        var old = AnnotationDocument.FromJson("""{"shapes":[{"type":"text","x":1,"y":2,"text":"Hi","background":true}]}""");
        Assert.Equal(TextPlate.Auto, ((TextShape)old.Shapes[0]).EffectivePlate);
    }

    [Fact]
    public void NewTextsAreTransparent() => Assert.Equal(TextPlate.None, new TextShape { Text = "Hi" }.EffectivePlate);
}
