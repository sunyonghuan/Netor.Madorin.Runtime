using System.Text;
using System.Text.Json;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Multimedia.Core.Files;

public sealed class RequestFileReader(WorkspacePaths paths)
{
    public async Task<JsonDocument> ReadAsync(string file, CancellationToken cancellationToken)
    {
        var path = paths.Resolve(file);
        try
        {
            paths.EnsureAllowed(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length > SkillProtocol.MaxRequestBytes)
            {
                throw new SkillException("invalid_parameter", "请求文件超过 1 MiB。");
            }

            // 多读一个字节用于检测打开文件后的增长，避免无界加载。
            var bytes = new byte[SkillProtocol.MaxRequestBytes + 1];
            var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellationToken);
            if (length > SkillProtocol.MaxRequestBytes)
            {
                throw new SkillException("invalid_parameter", "请求文件超过 1 MiB。");
            }

            var text = new UTF8Encoding(false, true).GetString(bytes, 0, length);
            if (text.StartsWith('\uFEFF'))
            {
                text = text[1..];
            }

            var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object || JsonProtocol.HasDuplicateProperties(document.RootElement))
            {
                document.Dispose();
                throw new SkillException("invalid_parameter", "请求 JSON 必须是无重复字段的对象。");
            }

            return document;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or IOException or UnauthorizedAccessException)
        {
            throw new SkillException("invalid_parameter", "请求文件无法读取或不是有效 UTF-8 JSON。");
        }
    }
}
