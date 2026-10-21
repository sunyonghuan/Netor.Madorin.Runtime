using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using SkiaSharp;

namespace Madorin.AI.Multimedia.Tests;

[TestClass]
public sealed class ImageValidationTests
{
    [TestMethod]
    [DataRow(SKEncodedImageFormat.Png, "png")]
    [DataRow(SKEncodedImageFormat.Jpeg, "jpeg")]
    [DataRow(SKEncodedImageFormat.Webp, "webp")]
    public async Task CompleteImagesDecodeWithTheirActualFormat(SKEncodedImageFormat encoding, string format)
    {
        var dimensions = await ImageValidator.ValidateAsync(ImageFixture.Create(encoding), format, CancellationToken.None);
        Assert.AreEqual((2, 2), dimensions);
    }

    [TestMethod]
    public async Task TruncatedAndMismatchedImagesNeverCreateArtifacts()
    {
        using var workspace = new TestWorkspace();
        using var store = new ArtifactStore(new WorkspacePaths(workspace.Root));
        var png = ImageFixture.Create();
        var damaged = png.ToArray();
        var header = "IDAT"u8;
        var offset = damaged.AsSpan().IndexOf(header);
        Assert.IsGreaterThan(0, offset);
        Array.Fill(damaged, (byte)0, offset + 4, 5);
        foreach (var image in new[] { png[..24], png[..^8], damaged, png })
        {
            var format = image == png ? "jpeg" : "png";
            var error = await Assert.ThrowsExactlyAsync<SkillException>(() => store.WriteImageAsync(image, format, "out", 1, CancellationToken.None));
            Assert.AreEqual("provider_error", error.Code);
        }
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "out")));
    }
}
