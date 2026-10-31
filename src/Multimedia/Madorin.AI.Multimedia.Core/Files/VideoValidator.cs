using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Multimedia.Core.Files;

/// <summary>通过媒体工具探测并完整解码 MP4，不以扩展名代替验证。</summary>
public static class VideoValidator
{
    public static void EnsureTools() => MediaToolProcess.EnsureAvailable();

    public static async Task<JsonObject> ValidateAsync(string file, CancellationToken cancellationToken)
    {
        var probe = await MediaToolProcess.RunAsync("ffprobe", ["-v", "error", "-protocol_whitelist", "file", "-show_entries",
            "format=format_name,duration:stream=codec_type,width,height", "-of", "json", file], cancellationToken);
        JsonDocument document;
        try { document = JsonDocument.Parse(probe); }
        catch (JsonException) { throw new SkillException("provider_error", "视频探测结果无效。"); }
        using (document)
        {
            var value = document.RootElement;
            if (!value.TryGetProperty("format", out var format) || !value.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array || JsonProtocol.GetString(format, "format_name")?.Split(',').Contains("mp4", StringComparer.Ordinal) != true ||
                !double.TryParse(JsonProtocol.GetString(format, "duration"), System.Globalization.CultureInfo.InvariantCulture, out var duration) ||
                !double.IsFinite(duration) || duration <= 0)
            {
                throw new SkillException("provider_error", "视频容器或时长无效。");
            }
            int? width = null;
            int? height = null;
            var audio = false;
            foreach (var stream in streams.EnumerateArray())
            {
                var type = JsonProtocol.GetString(stream, "codec_type");
                if (type == "audio") { audio = true; }
                if (type == "video" && width is null)
                {
                    width = JsonProtocol.GetInt(stream, "width");
                    height = JsonProtocol.GetInt(stream, "height");
                }
            }
            if (width is null or < 1 || height is null or < 1) { throw new SkillException("provider_error", "MP4 缺少有效视频轨道。"); }
            await MediaToolProcess.RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-xerror", "-err_detect", "explode", "-protocol_whitelist", "file",
                "-i", file, "-map", "0:v", "-map", "0:a?", "-f", "null", "-"], cancellationToken);
            return Obj(("width", N(width.Value)), ("height", N(height.Value)), ("duration_seconds", JsonValue.Create(duration)), ("has_audio", B(audio)));
        }
    }
}
