using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Kling.Video.Requests;

internal static class VideoImageMaterializer
{
    private const int MaxBytes = 30 * 1024 * 1024;

    public static async Task<string?> MaterializeAsync(VideoGenerationOptions options, WorkspacePaths paths, CancellationToken cancellationToken)
    {
        if (options.ImageKind is null) return null;
        if (options.ImageKind == "uri") return options.ImageValue;
        var full = paths.Resolve(options.ImageValue!, options.ImageBaseDirectory);
        paths.EnsureAllowed(full);
        if (!File.Exists(full)) throw new SkillException("invalid_parameter", "参考图片不存在。");
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > MaxBytes) throw new SkillException("invalid_parameter", "参考图片必须大于 0 且不超过 30 MiB。");
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        var format = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "png"
            : bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 ? "jpeg"
            : throw new SkillException("invalid_parameter", "参考图片只支持 png 或 jpeg。");
        (int Width, int Height) size;
        try
        {
            size = await ImageValidator.ValidateAsync(bytes, format, cancellationToken);
        }
        catch (SkillException)
        {
            throw new SkillException("invalid_parameter", "参考图片无法完整解码。");
        }

        var ratio = (double)size.Width / size.Height;
        if (size.Width < 300 || size.Height < 300 || ratio < 2d / 5d || ratio > 5d / 2d)
        {
            throw new SkillException("invalid_parameter", "参考图片边长不能小于 300，宽高比必须介于 2:5 和 5:2。");
        }

        // 可灵图生视频的 url 字段接受原始 Base64，不接受 data URI 前缀。
        return Convert.ToBase64String(bytes);
    }
}
