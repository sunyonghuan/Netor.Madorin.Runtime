using System.Reflection;
using System.Text;

namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>在支持 Sixel 的终端上把内置透明 logo 画成六行高。</summary>
internal static class ConsoleLogo
{
    private const int LogoRows = 6;
    private const string ResourceName = "Madorin.AI.Runtime.Cli.logo.png";

    public static bool TryWrite(TextWriter output, bool interactive, int leftMargin)
    {
        if (!interactive)
        {
            return false;
        }

        try
        {
            var logo = ReadLogo();
            if (logo is null)
            {
                return false;
            }

            output.Write("\u001b[c\u001b[16t");
            output.Flush();
            var response = ReadResponse();
            if (!SupportsSixel(response) || !TryReadCellSize(response, out _, out var cellHeight))
            {
                return false;
            }

            var image = PngRgbaDecoder.Decode(logo);
            var pixelHeight = Math.Max(LogoRows, cellHeight * LogoRows);
            var pixelWidth = Math.Max(1, (int)Math.Round(pixelHeight * (image.Width / (double)image.Height)));
            if (leftMargin > 0)
            {
                output.Write(new string(' ', leftMargin));
            }

            output.Write(SixelEncoder.Encode(image, pixelWidth, pixelHeight));
            output.Flush();
            if (Console.CursorLeft != 0)
            {
                output.WriteLine();
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            return false;
        }
    }

    internal static byte[]? ReadLogo()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var data = new MemoryStream();
        stream.CopyTo(data);
        return data.ToArray();
    }

    internal static bool SupportsSixel(string response)
    {
        var start = response.IndexOf('?', StringComparison.Ordinal);
        var end = response.IndexOf('c', StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            return false;
        }

        foreach (var part in response[(start + 1)..end].Split(';'))
        {
            if (part == "4")
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryReadCellSize(string response, out int width, out int height)
    {
        width = 0;
        height = 0;
        var marker = response.LastIndexOf("[6;", StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }

        var end = response.IndexOf('t', marker);
        if (end <= marker)
        {
            return false;
        }

        var parts = response[(marker + 3)..end].Split(';');
        return parts.Length == 2
            && int.TryParse(parts[0], out height)
            && int.TryParse(parts[1], out width)
            && width > 0
            && height > 0;
    }

    private static string ReadResponse()
    {
        var text = new StringBuilder();
        var deadline = Environment.TickCount64 + 300;
        while (Environment.TickCount64 < deadline)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(5);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            if (key.KeyChar != '\0')
            {
                text.Append(key.KeyChar);
            }

            var value = text.ToString();
            if (value.Contains('c') && value.Contains('t'))
            {
                break;
            }
        }

        return text.ToString();
    }
}
