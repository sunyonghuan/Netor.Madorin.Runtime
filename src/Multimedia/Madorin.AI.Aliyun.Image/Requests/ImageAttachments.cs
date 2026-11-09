using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Files;

namespace Madorin.AI.Aliyun.Image.Requests;

internal static class ImageAttachments
{
    public static (string[] Remaining, JsonArray? Attachments) Extract(string[] args) => RequestAttachments.Extract(args);
    public static void Validate(JsonNode? node, bool present, WorkspacePaths paths, string? baseDirectory) =>
        RequestAttachments.Validate(node, present, paths, baseDirectory);
}

