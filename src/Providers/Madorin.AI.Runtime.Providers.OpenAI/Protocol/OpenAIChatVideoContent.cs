using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Madorin.AI.Runtime.Providers.OpenAI.Protocol;

/// <summary>Encodes the Aliyun video_url extension without passing video through the SDK's image-only fallback.</summary>
internal static class OpenAIChatVideoContent
{
    public static ChatMessageContentPart Create(DataContent content)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "video_url");
            writer.WriteStartObject("video_url");
            writer.WriteString("url", content.Uri);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        // The SDK's reader rejects unknown types; patch the native JSON root for this vendor extension.
        var part = ChatMessageContentPart.CreateTextPart(string.Empty);
#pragma warning disable SCME0001
        part.Patch.Set("$"u8, buffer.ToArray());
#pragma warning restore SCME0001
        return part;
    }
}
