using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.DeepSeek.Models;
using Madorin.AI.Runtime.Providers.DeepSeek.Protocol;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Providers.DeepSeek;

/// <summary>
/// DeepSeek 专用 Provider Adapter。
/// 使用 OpenAI 兼容协议接入 DeepSeek 端点，
/// 通过 <see cref="DeepSeekOverrideHandler"/> 在 HTTP 层自动补写 reasoning_content，
/// 通过 <see cref="DeepSeekDelegatingChatClient"/> 在请求前缓存 reasoning 回传上下文。
/// 从老项目 <c>Netor.Cortana.AI/Drivers/Providers/Deepseek/</c> 迁移。
/// </summary>
public sealed class DeepSeekProviderAdapter : IRuntimeProviderAdapter, IDisposable
{
    private const string ProviderRequestFailed = nameof(ProviderRequestFailed);
    private const string ProviderRateLimited = nameof(ProviderRateLimited);

    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IChatClient> _chatClients =
        new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;

    public DeepSeekProviderAdapter(
        string baseUrl,
        string apiKey,
        ILoggerFactory? loggerFactory = null,
        IReadOnlyList<ProviderModel>? models = null,
        string providerId = "deepseek")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        ProviderId = providerId;
        Models = models ?? DeepSeekModelCatalog.All;

        // ── HTTP 管道：DeepSeekOverrideHandler → 实际 HTTP 发送 ──────────────
        var overrideHandler = new DeepSeekOverrideHandler(
            _loggerFactory.CreateLogger<DeepSeekOverrideHandler>());
        overrideHandler.InnerHandler = new HttpClientHandler();
        _httpClient = new HttpClient(overrideHandler);

        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl.TrimEnd('/')),
            NetworkTimeout = TimeSpan.FromMinutes(10),
            Transport = new HttpClientPipelineTransport(_httpClient),
            ClientLoggingOptions = new ClientLoggingOptions
            {
                EnableLogging = false,
                EnableMessageLogging = false,
                EnableMessageContentLogging = false,
                MessageContentSizeLimit = 1024
            }
        };

        var openAiClient = new OpenAIClient(credential, options);

        // Factory：每个 modelId 复用同一 IChatClient 实例（含 DeepSeekDelegatingChatClient 包装）
        _chatClientFactory = modelId => _chatClients.GetOrAdd(
            modelId,
            id => new DeepSeekDelegatingChatClient(
                openAiClient.GetChatClient(id).AsIChatClient()));
    }

    private readonly Func<string, IChatClient> _chatClientFactory;

    public string ProviderId { get; }

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        ToolCalling: true,
        Reasoning: true);

    public IReadOnlyList<ProviderModel> Models { get; }

    public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default)
        => ProviderCapabilityValidator.ValidateAsync(Capabilities, input, ct);

    public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProviderCapabilityValidator.ValidateSkillsSupport(Capabilities, request.Skills, ct);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            request.CancellationToken);
        var effectiveCt = linkedCts.Token;
        await ValidateCapabilitiesAsync(
            FlattenContent(request.Messages),
            effectiveCt).ConfigureAwait(false);
        var chatClient = _chatClientFactory(request.ModelId);
        var messages = ConvertMessages(request.Messages, request.Tools);
        var options = new ChatOptions
        {
            ModelId = request.ModelId,
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxTokens,
            Tools = CreateTools(request.Tools)
        };

        // Apply full ExpertRequestControl (centralized, cross-checked with DeepSeek OpenAI-compatible API)
        MafSkillsAgentFactory.ApplyRequestControl(options, request.RequestControl);

        string finishReason = "stop";
        RuntimeError? failure = null;

        await foreach (var update in MafSkillsAgentFactory.CreateResponseStream(
                               chatClient,
                               request,
                               messages,
                               options,
                               effectiveCt)
                           .ConfigureAwait(false))
        {
            if (update.FinishReason is { } reason)
            {
                finishReason = reason.ToString();
            }

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        yield return new ReasoningDeltaProviderEvent(request.InvocationId, reasoning.Text);
                        break;

                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        yield return new TextDeltaProviderEvent(request.InvocationId, text.Text);
                        break;

                    case FunctionCallContent functionCall:
                        var argumentsJson = SerializeArguments(functionCall.Arguments);
                        var callId = functionCall.CallId ?? string.Empty;
                        var name = functionCall.Name ?? string.Empty;
                        var toolId = ResolveToolId(request.Tools, name);

                        yield return new ToolCallDeltaProviderEvent(
                            request.InvocationId,
                            callId,
                            name,
                            argumentsJson);
                        yield return new ToolCallCompleteProviderEvent(
                            request.InvocationId,
                            callId,
                            toolId,
                            name,
                            argumentsJson);
                        break;

                    case UsageContent usage when usage.Details is not null:
                        yield return new UsageUpdatedProviderEvent(
                            request.InvocationId,
                            (int)(usage.Details.InputTokenCount ?? 0),
                            (int)(usage.Details.OutputTokenCount ?? 0));
                        break;
                }
            }
        }

        if (failure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
        }
        else
        {
            yield return new InvocationCompletedProviderEvent(request.InvocationId, finishReason);
        }
    }

    private static ChatMessage[] ConvertMessages(
        RuntimeProviderMessage[] source,
        ToolDescriptor[]? tools)
    {
        var messages = new List<ChatMessage>(source.Length);
        foreach (var message in source)
        {
            var content = new List<AIContent>(message.Content.Length);
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextContentBlock text:
                        content.Add(new TextContent(text.Text));
                        break;
                    case ReasoningContentBlock reasoning:
                        content.Add(new TextReasoningContent(reasoning.Content));
                        break;
                    case ToolCallContentBlock toolCall:
                        content.Add(new FunctionCallContent(
                            toolCall.CallId,
                            ProviderToolNamePolicy.GetHistoryName(
                                tools,
                                toolCall.ToolId,
                                toolCall.Name),
                            ConvertArguments(toolCall.Arguments)));
                        break;
                    case ToolResultContentBlock toolResult:
                        content.Add(new FunctionResultContent(
                            toolResult.CallId,
                            ConvertToolResult(toolResult)));
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported content block '{block.GetType().Name}'.");
                }
            }

            messages.Add(new ChatMessage(ConvertRole(message.Role), content));
        }

        return [.. messages];
    }

    private static List<AITool>? CreateTools(ToolDescriptor[]? tools)
    {
        if (tools is not { Length: > 0 })
        {
            return null;
        }

        ProviderToolNamePolicy.ValidateUniqueNames(tools);
        var declarations = new List<AITool>(tools.Length);
        foreach (var tool in tools)
        {
            using var schema = JsonDocument.Parse(tool.InputSchemaJson);
            declarations.Add(AIFunctionFactory.CreateDeclaration(
                ProviderToolNamePolicy.GetName(tool),
                tool.Description,
                schema.RootElement.Clone()));
        }

        return declarations;
    }

    private static ChatRole ConvertRole(string role) => role switch
    {
        RuntimeProviderRoles.System => ChatRole.System,
        RuntimeProviderRoles.User => ChatRole.User,
        RuntimeProviderRoles.Assistant => ChatRole.Assistant,
        RuntimeProviderRoles.Tool => ChatRole.Tool,
        _ => throw new InvalidOperationException($"Unsupported Provider message role '{role}'.")
    };

    private static Dictionary<string, object?> ConvertArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = arguments.Clone()
            };
        }

        var converted = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            converted[property.Name] = property.Value.Clone();
        }

        return converted;
    }

    private static object ConvertToolResult(ToolResultContentBlock toolResult)
    {
        if (toolResult.Content is [TextContentBlock text])
        {
            return text.Text;
        }

        return JsonSerializer.SerializeToElement(
            toolResult.Content,
            RuntimeJsonContext.Default.ContentBlockArray);
    }

    private static string ResolveToolId(ToolDescriptor[]? tools, string name) =>
        ProviderToolNamePolicy.ResolveToolId(tools, name);

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (arguments is not null)
            {
                foreach (var argument in arguments)
                {
                    writer.WritePropertyName(argument.Key);
                    WriteJsonValue(writer, argument.Value);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case JsonNode node:
                node.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static ContentBlock[] FlattenContent(RuntimeProviderMessage[] messages) =>
        [.. messages.SelectMany(static message => message.Content)];

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
