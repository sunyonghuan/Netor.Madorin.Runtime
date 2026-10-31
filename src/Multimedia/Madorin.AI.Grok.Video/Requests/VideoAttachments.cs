using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Files;

namespace Madorin.AI.Grok.Video.Requests;

/// <summary>文生视频不读取媒体；复用公共附件校验后按原索引记录不支持项。</summary>
internal static class VideoAttachments
{
    public static (JsonArray? Attachments, string[] Remaining) Extract(string[] args)
    {
        var extracted = RequestAttachments.Extract(args);
        return (extracted.Attachments, extracted.Remaining);
    }

    public static void Validate(JsonNode? node, bool present, WorkspacePaths paths, string? baseDirectory) =>
        RequestAttachments.Validate(node, present, paths, baseDirectory);
}
