using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Aliyun.Video.Requests;

internal static class VideoImageMaterializer
{
    private const int MaxBytes = 20 * 1024 * 1024;

    public static async Task<string?> MaterializeAsync(VideoGenerationOptions options, WorkspacePaths paths, CancellationToken cancellationToken)
    {
        if (options.ImageKind is null) return null;
        if (options.ImageKind == "uri") return options.ImageValue;
        var full = paths.Resolve(options.ImageValue!, options.ImageBaseDirectory);
        paths.EnsureAllowed(full);
        if (!File.Exists(full)) throw new SkillException("invalid_parameter", "首帧图片不存在。");
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > MaxBytes) throw new SkillException("invalid_parameter", "首帧图片必须大于 0 且不超过 20 MiB。");
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        var format = Detect(bytes);
        int width;
        int height;
        try
        {
            (width, height) = await ImageValidator.ValidateAsync(bytes, format, cancellationToken);
        }
        catch (SkillException)
        {
            throw new SkillException("invalid_parameter", "本地首帧必须是完整可解码的 png、jpeg 或 webp。");
        }

        if (width is < 240 or > 8000 || height is < 240 or > 8000 || Math.Max(width, height) > Math.Min(width, height) * 8L)
        {
            throw new SkillException("invalid_parameter", "首帧宽高必须在 240 至 8000 像素，且宽高比不超过 8:1。");
        }

        var mime = format == "png" ? "image/png" : format == "webp" ? "image/webp" : "image/jpeg";
        return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
    }

    private static string Detect(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "png";
        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return "jpeg";
        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "webp";
        throw new SkillException("invalid_parameter", "本地首帧只支持 png、jpeg 或 webp；BMP 与临时 OSS 地址本批不读取。");
    }
}
