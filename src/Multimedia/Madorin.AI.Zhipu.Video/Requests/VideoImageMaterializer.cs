using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Zhipu.Video.Requests;

internal static class VideoImageMaterializer
{
    private const int MaxBytes = 5 * 1024 * 1024;

    public static async Task<string?> MaterializeAsync(VideoGenerationOptions options, WorkspacePaths paths, CancellationToken cancellationToken)
    {
        if (options.ImageKind is null) return null;
        if (options.ImageKind == "uri") return options.ImageValue;
        var full = paths.Resolve(options.ImageValue!, options.ImageBaseDirectory);
        paths.EnsureAllowed(full);
        if (!File.Exists(full)) throw new SkillException("invalid_parameter", "参考图片不存在。");
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > MaxBytes) throw new SkillException("invalid_parameter", "参考图片必须大于 0 且不超过 5 MiB。");
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        var mime = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
            : bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 ? "image/jpeg"
            : throw new SkillException("invalid_parameter", "参考图片只支持 png 或 jpeg。");
        return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
    }
}
