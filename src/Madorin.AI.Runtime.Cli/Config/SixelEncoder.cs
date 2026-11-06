using System.Text;

namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>把缩小后的 RGBA 图片编码成透明背景的 Sixel。</summary>
internal static class SixelEncoder
{
    public static string Encode(RgbaImage source, int width, int height)
    {
        var image = Scale(source, width, height);
        var colors = new Dictionary<int, int>();
        var indexed = new int[width * height];
        for (var i = 0; i < indexed.Length; i++)
        {
            var offset = i * 4;
            if (image.Pixels[offset + 3] < 128)
            {
                indexed[i] = -1;
                continue;
            }

            var key = Quantize(image.Pixels[offset]) << 16
                | Quantize(image.Pixels[offset + 1]) << 8
                | Quantize(image.Pixels[offset + 2]);
            if (!colors.TryGetValue(key, out var index))
            {
                index = colors.Count;
                colors.Add(key, index);
            }

            indexed[i] = index;
        }

        if (colors.Count == 0)
        {
            throw new InvalidDataException("Logo has no visible pixels.");
        }

        var sixel = new StringBuilder();
        sixel.Append('\u001b').Append("P0;1q\"1;1;").Append(width).Append(';').Append(height);
        foreach (var color in colors)
        {
            sixel.Append('#').Append(color.Value).Append(";2;")
                .Append(Percent((color.Key >> 16) & 255)).Append(';')
                .Append(Percent((color.Key >> 8) & 255)).Append(';')
                .Append(Percent(color.Key & 255));
        }

        var row = new char[width];
        for (var band = 0; band < height; band += 6)
        {
            var drewColor = false;
            foreach (var color in colors.Values)
            {
                var visible = false;
                for (var x = 0; x < width; x++)
                {
                    var bits = 0;
                    for (var dy = 0; dy < 6; dy++)
                    {
                        var y = band + dy;
                        if (y < height && indexed[(y * width) + x] == color)
                        {
                            bits |= 1 << dy;
                            visible = true;
                        }
                    }

                    row[x] = (char)(63 + bits);
                }

                if (!visible)
                {
                    continue;
                }

                if (drewColor)
                {
                    sixel.Append('$');
                }

                drewColor = true;
                sixel.Append('#').Append(color);
                AppendRun(sixel, row);
            }

            sixel.Append('-');
        }

        sixel.Append('\u001b').Append('\\');
        return sixel.ToString();
    }

    private static void AppendRun(StringBuilder sixel, char[] row)
    {
        var index = 0;
        while (index < row.Length)
        {
            var value = row[index];
            var count = 1;
            while (index + count < row.Length && row[index + count] == value)
            {
                count++;
            }

            if (count >= 3)
            {
                sixel.Append('!').Append(count).Append(value);
            }
            else
            {
                sixel.Append(value, count);
            }

            index += count;
        }
    }

    private static RgbaImage Scale(RgbaImage source, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceY = ((y + 0.5d) * source.Height / height) - 0.5d;
            var top = Math.Clamp((int)Math.Floor(sourceY), 0, source.Height - 1);
            var bottom = Math.Min(top + 1, source.Height - 1);
            var yWeight = sourceY - top;
            for (var x = 0; x < width; x++)
            {
                var sourceX = ((x + 0.5d) * source.Width / width) - 0.5d;
                var left = Math.Clamp((int)Math.Floor(sourceX), 0, source.Width - 1);
                var right = Math.Min(left + 1, source.Width - 1);
                var xWeight = sourceX - left;
                var target = ((y * width) + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var topLeft = source.Pixels[((top * source.Width) + left) * 4 + channel];
                    var topRight = source.Pixels[((top * source.Width) + right) * 4 + channel];
                    var bottomLeft = source.Pixels[((bottom * source.Width) + left) * 4 + channel];
                    var bottomRight = source.Pixels[((bottom * source.Width) + right) * 4 + channel];
                    var blended = (topLeft * (1 - xWeight) + topRight * xWeight) * (1 - yWeight)
                        + (bottomLeft * (1 - xWeight) + bottomRight * xWeight) * yWeight;
                    pixels[target + channel] = (byte)Math.Clamp((int)Math.Round(blended), 0, 255);
                }
            }
        }

        return new RgbaImage(width, height, pixels);
    }

    private static int Quantize(byte value) => value / 8 * 8;

    private static int Percent(int value) => (value * 100 + 127) / 255;
}
