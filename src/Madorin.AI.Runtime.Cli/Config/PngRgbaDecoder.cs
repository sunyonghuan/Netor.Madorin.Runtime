using System.IO.Compression;

namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>只解析欢迎 logo 这种 8 位、非隔行扫描的 RGBA PNG。</summary>
internal static class PngRgbaDecoder
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static RgbaImage Decode(ReadOnlySpan<byte> png)
    {
        if (!png.StartsWith(Signature))
        {
            throw new InvalidDataException("Logo is not a PNG.");
        }

        var width = 0;
        var height = 0;
        var data = new MemoryStream();
        var offset = Signature.Length;
        while (offset + 8 <= png.Length)
        {
            var length = ReadInt32(png, offset);
            var type = png.Slice(offset + 4, 4);
            var chunk = png.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = ReadInt32(chunk, 0);
                height = ReadInt32(chunk, 4);
                if (chunk[8] != 8 || chunk[9] != 6 || chunk[10] != 0 || chunk[11] != 0 || chunk[12] != 0)
                {
                    throw new InvalidDataException("Logo PNG must be 8-bit non-interlaced RGBA.");
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                data.Write(chunk);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += 12 + length;
        }

        if (width <= 0 || height <= 0 || data.Length == 0)
        {
            throw new InvalidDataException("Logo PNG is missing image data.");
        }

        data.Position = 0;
        using var zlib = new ZLibStream(data, CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        zlib.CopyTo(inflated);
        return Unfilter(inflated.ToArray(), width, height);
    }

    private static RgbaImage Unfilter(byte[] data, int width, int height)
    {
        var stride = width * 4;
        if (data.Length < height * (stride + 1))
        {
            throw new InvalidDataException("Logo PNG data is incomplete.");
        }

        var pixels = new byte[height * stride];
        var source = 0;
        for (var y = 0; y < height; y++)
        {
            var filter = data[source++];
            var row = y * stride;
            for (var x = 0; x < stride; x++)
            {
                var raw = data[source++];
                var left = x >= 4 ? pixels[row + x - 4] : (byte)0;
                var up = y > 0 ? pixels[row - stride + x] : (byte)0;
                var upLeft = y > 0 && x >= 4 ? pixels[row - stride + x - 4] : (byte)0;
                pixels[row + x] = (byte)(filter switch
                {
                    0 => raw,
                    1 => raw + left,
                    2 => raw + up,
                    3 => raw + ((left + up) / 2),
                    4 => raw + Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException("Logo PNG uses an unknown filter.")
                });
            }
        }

        return new RgbaImage(width, height, pixels);
    }

    private static byte Paeth(byte left, byte up, byte upLeft)
    {
        var estimate = left + up - upLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var diagonalDistance = Math.Abs(estimate - upLeft);
        if (leftDistance <= upDistance && leftDistance <= diagonalDistance)
        {
            return left;
        }

        return upDistance <= diagonalDistance ? up : upLeft;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}

internal readonly struct RgbaImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Pixels { get; } = pixels;
}
