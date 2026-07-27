using System.Text;

namespace Madorin.AI.Runtime.Tools.Builtin;

internal static class FileSystemTextEncoding
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LittleEndianBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianBom = [0xFE, 0xFF];

    static FileSystemTextEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static DecodedText Decode(ReadOnlySpan<byte> bytes, string requestedEncoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedEncoding);
        var detectedBom = DetectBom(bytes);
        var encodingName = NormalizeName(requestedEncoding);
        if (string.Equals(encodingName, "auto", StringComparison.Ordinal))
        {
            encodingName = DetectEncoding(bytes, detectedBom);
        }

        var encoding = CreateEncoding(encodingName, emitBom: false);
        if (detectedBom is { } bom
            && !IsMatchingBom(encoding.CodePage, bom.CodePage))
        {
            throw new InvalidDataException(
                $"The file BOM does not match encoding '{requestedEncoding}'.");
        }

        var preambleLength = detectedBom?.Length ?? 0;
        try
        {
            var content = encoding.GetString(bytes[preambleLength..]);
            return new DecodedText(
                content,
                encodingName,
                encoding.CodePage,
                detectedBom is not null,
                DetectLineEnding(content),
                EndsWithNewline(content));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"The file is not valid {encodingName} text.",
                ex);
        }
    }

    public static byte[] Encode(
        string content,
        string requestedEncoding,
        bool includeBom)
    {
        ArgumentNullException.ThrowIfNull(content);
        var encodingName = NormalizeName(requestedEncoding);
        if (string.Equals(encodingName, "auto", StringComparison.Ordinal))
        {
            throw new ArgumentException("Encoding 'auto' cannot be used for writing.");
        }

        var encoding = CreateEncoding(encodingName, includeBom);
        try
        {
            var preamble = includeBom ? encoding.GetPreamble() : [];
            var body = encoding.GetBytes(content);
            if (preamble.Length == 0)
            {
                return body;
            }

            var bytes = new byte[preamble.Length + body.Length];
            preamble.CopyTo(bytes, 0);
            body.CopyTo(bytes, preamble.Length);
            return bytes;
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"The content cannot be represented as {encodingName} text.",
                ex);
        }
    }

    public static string NormalizeLineEndings(
        string content,
        string requestedLineEnding,
        string existingLineEnding)
    {
        ArgumentNullException.ThrowIfNull(content);
        var mode = string.IsNullOrWhiteSpace(requestedLineEnding)
            ? "preserve"
            : requestedLineEnding.Trim().ToLowerInvariant();
        var target = mode switch
        {
            "preserve" => existingLineEnding switch
            {
                "crlf" => "\r\n",
                "cr" => "\r",
                _ => "\n"
            },
            "lf" => "\n",
            "crlf" => "\r\n",
            _ => throw new ArgumentException(
                "Line ending must be preserve, lf, or crlf.")
        };

        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", target, StringComparison.Ordinal);
    }

    private static string DetectEncoding(ReadOnlySpan<byte> bytes, BomInfo? bom)
    {
        if (bom is not null)
        {
            return bom.Value.CodePage switch
            {
                65001 => "utf-8",
                1200 => "utf-16le",
                1201 => "utf-16be",
                _ => throw new InvalidDataException("The file BOM is not supported.")
            };
        }

        if (IsAscii(bytes))
        {
            return "ascii";
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return "utf-8";
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                "Encoding is ambiguous. Specify gb2312, gbk, gb18030, or another encoding.",
                ex);
        }
    }

    private static Encoding CreateEncoding(string name, bool emitBom) => name switch
    {
        "utf-8" => new UTF8Encoding(emitBom, true),
        "utf-16le" => new UnicodeEncoding(false, emitBom, true),
        "utf-16be" => new UnicodeEncoding(true, emitBom, true),
        "gb2312" or "gbk" => Encoding.GetEncoding(
            936,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback),
        "gb18030" => Encoding.GetEncoding(
            54936,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback),
        "ascii" => Encoding.GetEncoding(
            20127,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback),
        "latin1" => Encoding.GetEncoding(
            28591,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback),
        _ => throw new ArgumentException($"Encoding '{name}' is not supported.")
    };

    private static string NormalizeName(string name) => name.Trim().ToLowerInvariant() switch
    {
        "utf8" or "utf-8" or "utf-8-bom" => "utf-8",
        "unicode" or "utf16" or "utf-16" or "utf-16le" => "utf-16le",
        "utf-16be" => "utf-16be",
        "gb2312" => "gb2312",
        "gbk" or "cp936" => "gbk",
        "gb18030" => "gb18030",
        "ascii" or "us-ascii" => "ascii",
        "latin1" or "iso-8859-1" => "latin1",
        "auto" => "auto",
        _ => throw new ArgumentException($"Encoding '{name}' is not supported.")
    };

    private static bool IsAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value >= 0x80)
            {
                return false;
            }
        }

        return true;
    }

    private static BomInfo? DetectBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8Bom))
        {
            return new BomInfo(65001, Utf8Bom.Length);
        }

        if (bytes.StartsWith(Utf16LittleEndianBom))
        {
            return new BomInfo(1200, Utf16LittleEndianBom.Length);
        }

        return bytes.StartsWith(Utf16BigEndianBom)
            ? new BomInfo(1201, Utf16BigEndianBom.Length)
            : null;
    }

    private static bool IsMatchingBom(int encodingCodePage, int bomCodePage) =>
        encodingCodePage == bomCodePage;

    private static string DetectLineEnding(string content)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r')
            {
                if (index + 1 < content.Length && content[index + 1] == '\n')
                {
                    crlf++;
                    index++;
                }
                else
                {
                    cr++;
                }
            }
            else if (content[index] == '\n')
            {
                lf++;
            }
        }

        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        if (kinds > 1)
        {
            return "mixed";
        }

        if (crlf > 0)
        {
            return "crlf";
        }

        if (lf > 0)
        {
            return "lf";
        }

        return cr > 0 ? "cr" : "none";
    }

    private static bool EndsWithNewline(string content) =>
        content.EndsWith('\n') || content.EndsWith('\r');

    private readonly record struct BomInfo(int CodePage, int Length);
}

internal sealed record DecodedText(
    string Content,
    string Encoding,
    int CodePage,
    bool HasBom,
    string LineEnding,
    bool EndsWithNewline);
