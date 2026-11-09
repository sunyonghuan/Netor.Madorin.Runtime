using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class ConsoleLogoTests
{
    private const string ProbeResponse = "\u001b[?61;4;6;7;14;21;22;23;24;28;32;42;52c\u001b[6;20;10t";

    [TestMethod]
    public void SupportsSixel_RequiresExactAttributeFour()
    {
        Assert.IsTrue(ConsoleLogo.SupportsSixel(ProbeResponse));
        Assert.IsFalse(ConsoleLogo.SupportsSixel("\u001b[?61;14;24c"));
    }

    [TestMethod]
    public void TryReadCellSize_ReadsTerminalCellPixels()
    {
        Assert.IsTrue(ConsoleLogo.TryReadCellSize(ProbeResponse, out var width, out var height));
        Assert.AreEqual(10, width);
        Assert.AreEqual(20, height);
    }

    [TestMethod]
    public void Logo_DecodesTransparentPngAndEncodesSixel()
    {
        var logo = ConsoleLogo.ReadLogo();
        Assert.IsNotNull(logo);
        var image = PngRgbaDecoder.Decode(logo);
        Assert.AreEqual(130, image.Width);
        Assert.AreEqual(130, image.Height);
        Assert.AreEqual(0, image.Pixels[3]);

        var sixel = SixelEncoder.Encode(image, 120, 120);
        Assert.Contains("\u001bP0;1q\"1;1;120;120", sixel, StringComparison.Ordinal);
        Assert.EndsWith("\u001b\\", sixel, StringComparison.Ordinal);
    }
}
