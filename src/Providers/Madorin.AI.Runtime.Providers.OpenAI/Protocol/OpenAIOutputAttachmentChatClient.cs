using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Madorin.AI.Runtime.Providers.OpenAI.Protocol;

/// <summary>Recovers completed native image results omitted by the SDK's streaming content mapper.</summary>
internal sealed class OpenAIOutputAttachmentChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            if (update.RawRepresentation is StreamingResponseOutputItemDoneUpdate
                { Item: ImageGenerationCallResponseItem image } && image.ImageResultBytes is { } bytes)
            {
                update.Contents.Add(new DataContent(bytes.ToMemory(), DetectImageType(bytes.ToMemory().Span)));
            }
            yield return update;
        }
    }

    private static string DetectImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 255, 216, 255 }))
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return "image/png";
    }
}
