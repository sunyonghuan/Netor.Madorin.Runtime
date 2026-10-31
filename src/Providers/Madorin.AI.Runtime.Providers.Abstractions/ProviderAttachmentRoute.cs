namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>The wire format used to encode attachment content, independent of model capability flags.</summary>
public enum ProviderAttachmentRoute
{
    OpenAIResponses,
    OpenAIChatCompletions,
    AnthropicMessages,
    AliyunChatCompletions
}
