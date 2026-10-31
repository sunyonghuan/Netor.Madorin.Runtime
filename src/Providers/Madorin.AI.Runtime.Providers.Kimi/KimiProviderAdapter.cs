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
using Madorin.AI.Runtime.Providers.Kimi.Models;
using Madorin.AI.Runtime.Providers.Kimi.Protocol;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Providers.Kimi;

/// <summary>
/// Kimi（Moonshot AI）专用 Provider Adapter。
/// 使用 OpenAI 兼容协议接入 Kimi 端点，
/// 通过 <see cref="KimiOverrideHandler"/> 自动注入 thinking 参数并补写 partial 字段。
/// 从老项目 <c>Netor.Cortana.AI/Drivers/Providers/Kimi/</c> 迁移。
/// </summary>
public sealed class KimiProviderAdapter : IRuntimeProviderAdapter
{
    private const int DefaultMaxTools = 128;
    private const string ProviderRequestFailed = nameof(ProviderRequestFailed);
    private const string ProviderRateLimited = nameof(ProviderRateLimited);

    private static readonly string DefaultBaseUrl = "https://api.moonshot.cn/v1";

    private readonly ConcurrentDictionary<string, IChatClient> _chatClients =
        new(StringComparer.Ordinal);
    private static readonly Action<ILogger, int, int, string, Exception?> ToolsTrimmedLog =
        LoggerMessage.Define<int, int, string>(
            LogLevel.Warning,
            new EventId(1, "KimiToolsTrimmed"),
            "Kimi tool definitions were trimmed from {OriginalCount} to {RetainedCount}; removed tools: {RemovedTools}");

    private readonly Func<string, IChatClient> _chatClientFactory;
    private readonly ILogger<KimiProviderAdapter> _logger;
    private readonly int _maxTools;
    private readonly IProviderBlobResolver? _blobResolver;

    public KimiProviderAdapter(
        string apiKey,
        string? baseUrl = null,
        ILoggerFactory? loggerFactory = null,
        IReadOnlyList<ProviderModel>? models = null,
        string providerId = "kimi",
        int? maxTools = DefaultMaxTools,
        IProviderBlobResolver? blobResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (maxTools is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTools),
                "Max tools must be zero, a positive integer, or null.");
        }

        var logFactory = loggerFactory
            ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _logger = logFactory.CreateLogger<KimiProviderAdapter>();
        _maxTools = maxTools ?? DefaultMaxTools;
        _blobResolver = blobResolver;
        Models = models ?? KimiModelCatalog.All;
        ProviderId = providerId;

        // ── HTTP 管道：KimiOverrideHandler → 实际 HTTP 发送 ──────────────────
        var overrideHandler = new KimiOverrideHandler(
            logFactory.CreateLogger<KimiOverrideHandler>());
        overrideHandler.InnerHandler = new HttpClientHandler();
        var httpClient = new HttpClient(overrideHandler);

        var credential = new ApiKeyCredential(apiKey);
        var endpoint = new Uri((baseUrl ?? DefaultBaseUrl).TrimEnd('/'));
        var options = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromMinutes(10),
            Transport = new HttpClientPipelineTransport(httpClient),
            ClientLoggingOptions = new ClientLoggingOptions
            {
                EnableLogging = false,
                EnableMessageLogging = false,
                EnableMessageContentLogging = false,
                MessageContentSizeLimit = 1024
            }
        };

        var openAiClient = new OpenAIClient(credential, options);

        _chatClientFactory = modelId => _chatClients.GetOrAdd(
            modelId,
            id => openAiClient.GetChatClient(id).AsIChatClient());
    }

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
        ChatMessage[]? messages = null;
        RuntimeError? attachmentFailure = null;
        try
        {
            messages = await ConvertMessagesAsync(request.Messages, request.Tools, effectiveCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (effectiveCt.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            attachmentFailure = ex is RuntimeProviderException runtimeException
                ? runtimeException.Error : ProviderErrorFactory.Create("Kimi", ex);
        }

        if (attachmentFailure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, attachmentFailure);
            yield break;
        }
        var options = new ChatOptions
        {
            ModelId = request.ModelId,
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxTokens,
            Tools = CreateTools(request.Tools)
        };

        // Apply full ExpertRequestControl.
        // Moonshot / Kimi (OpenAI-compatible): supports temperature, top_p, max_tokens, stop,
        // frequency_penalty, presence_penalty. Seed support is region-dependent.
        // Source of truth: latest Moonshot OpenAI-compatible chat completions API.
        MafSkillsAgentFactory.ApplyRequestControl(options, request.RequestControl);

        string finishReason = "stop";

        await foreach (var update in MafSkillsAgentFactory.CreateResponseStream(
                               chatClient,
                               request,
                               messages!,
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

                    case ProviderOutputAttachmentContent attachment:
                        yield return new AttachmentCompletedProviderEvent(request.InvocationId,
                            attachment.Blob, attachment.FileName);
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

        yield return new InvocationCompletedProviderEvent(request.InvocationId, finishReason);
    }

    private async ValueTask<ChatMessage[]> ConvertMessagesAsync(
        RuntimeProviderMessage[] source,
        ToolDescriptor[]? tools,
        CancellationToken ct)
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
                            await ProviderToolResultMapper.ToContentAsync(
                                toolResult, ResolveBlobAsync, ct).ConfigureAwait(false)));
                        break;
                    case BlobRefContentBlock blob:
                        content.Add(await ResolveBlobAsync(blob.Blob, ct).ConfigureAwait(false));
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported content block '{block.GetType().Name}'.");
                }
            }

            messages.Add(new ChatMessage(ConvertRole(message.Role), content));
        }

        return ProviderToolResultMapper.ProjectForChatCompletions([.. messages]);
    }

    private ValueTask<DataContent> ResolveBlobAsync(BlobReference blob, CancellationToken ct) =>
        ProviderBlobContentResolver.ResolveAsync(_blobResolver, blob, ProviderAttachmentRoute.OpenAIChatCompletions, ct);

    private List<AITool>? CreateTools(ToolDescriptor[]? tools)
    {
        if (tools is not { Length: > 0 })
        {
            return null;
        }

        ProviderToolNamePolicy.ValidateUniqueNames(tools);
        var retainedCount = _maxTools > 0
            ? Math.Min(tools.Length, _maxTools)
            : tools.Length;
        if (retainedCount < tools.Length)
        {
            var removedNames = tools
                .Skip(retainedCount)
                .Take(20)
                .Select(ProviderToolNamePolicy.GetName);
            ToolsTrimmedLog(
                _logger,
                tools.Length,
                retainedCount,
                string.Join(", ", removedNames),
                null);
        }

        var declarations = new List<AITool>(retainedCount);
        for (var index = 0; index < retainedCount; index++)
        {
            var tool = tools[index];
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
}
