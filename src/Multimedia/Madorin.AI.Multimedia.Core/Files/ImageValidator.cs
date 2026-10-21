using Madorin.AI.Multimedia.Core.Protocol;
using SkiaSharp;

namespace Madorin.AI.Multimedia.Core.Files;

public static class ImageValidator
{
    public static async Task<(int Width, int Height)> ValidateAsync(byte[] bytes, string expectedFormat, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasCompleteContainer(bytes, expectedFormat))
        {
            throw Invalid();
        }

        return await Task.Run(() => Decode(bytes, expectedFormat, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (int Width, int Height) Decode(byte[] bytes, string expectedFormat, CancellationToken cancellationToken)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || !string.Equals(codec.EncodedFormat.ToString(), expectedFormat, StringComparison.OrdinalIgnoreCase) || codec.Info.Width < 1 || codec.Info.Height < 1)
        {
            throw Invalid();
        }

        using var bitmap = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (bitmap.GetPixels() == IntPtr.Zero || codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) { throw Invalid(); }
        cancellationToken.ThrowIfCancellationRequested();
        return (bitmap.Width, bitmap.Height);
    }

    private static bool HasCompleteContainer(byte[] bytes, string format) => format switch
    {
        "png" => bytes.Length >= 45 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes.AsSpan(bytes.Length - 12).SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 }),
        "jpeg" => bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[^2] == 0xff && bytes[^1] == 0xd9,
        "webp" => bytes.Length >= 20 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) &&
            (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) + 8 == bytes.LongLength,
        _ => false
    };

    private static SkillException Invalid() => new("provider_error", "返回的图片不完整、无法解码或与请求编码格式不一致。");
}
