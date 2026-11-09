using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;
using Netor.Anthropic.Minimal;
using Netor.Anthropic.Minimal.Extensions;

namespace Madorin.AI.Runtime.Providers.Anthropic;

public sealed class AnthropicProviderAdapter :
    IRuntimeProviderAdapter,
    IRuntimeProviderCredentialUpdater,
    IDisposable
{
    private static readonly ReasoningSignatureCache Signatures = new();

    private readonly Func<string, IChatClient> _chatClientFactory;
    private readonly ConcurrentDictionary<string, IChatClient> _chatClients =
        new(StringComparer.Ordinal);
    private readonly IProviderBlobResolver? _blobResolver;
    private readonly ProviderAuthenticationHandler? _credentialHandler;
    private readonly AnthropicMinimalClient? _ownedClient;
    private readonly HttpClient? _fileClient;
    private readonly ProviderAuthenticationHandler? _fileCredentialHandler;
    private readonly Uri? _fileBaseUri;

    public AnthropicProviderAdapter(
        IChatClient chatClient,
        IReadOnlyList<ProviderModel>? models = null,
        IProviderBlobResolver? blobResolver = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        _chatClientFactory = _ => chatClient;
        _blobResolver = blobResolver;
        ProviderId = "anthropic";
        Models = models ?? [];
    }

    public AnthropicProviderAdapter(
        string apiKey,
        string baseUrl,
        IReadOnlyList<ProviderModel>? models = null,
        IProviderBlobResolver? blobResolver = null)
        : this("anthropic", apiKey, baseUrl, models, blobResolver)
    {
    }

    public AnthropicProviderAdapter(
        string providerId,
        string apiKey,
        string baseUrl,
        IReadOnlyList<ProviderModel>? models,
        IProviderBlobResolver? blobResolver = null)
        : this(
            providerId,
            apiKey,
            baseUrl,
            ProviderHttpClientFactory.Shared.CreateClient(providerId),
            disposeHttpClient: true,
            models,
            blobResolver)
    {
    }

    public AnthropicProviderAdapter(
        string providerId,
        string apiKey,
        string baseUrl,
        IHttpClientFactory httpClientFactory,
        IReadOnlyList<ProviderModel>? models = null,
        IProviderBlobResolver? blobResolver = null)
        : this(
            providerId,
            apiKey,
            baseUrl,
            httpClientFactory.CreateClient(providerId),
            disposeHttpClient: false,
            models,
            blobResolver)
    {
    }

    private AnthropicProviderAdapter(
        string providerId,
        string apiKey,
        string baseUrl,
        HttpClient httpClient,
        bool disposeHttpClient,
        IReadOnlyList<ProviderModel>? models,
        IProviderBlobResolver? blobResolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        ArgumentNullException.ThrowIfNull(httpClient);

        _blobResolver = blobResolver;
        _credentialHandler = new ProviderAuthenticationHandler(
            "x-api-key",
            scheme: null,
            apiKey,
            new HttpClientForwardingHandler(httpClient, disposeHttpClient));
        var providerHttpClient = new HttpClient(
            new ProviderHttpResponseHandler(_credentialHandler),
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var options = new AnthropicMinimalClientOptions
        {
            // ProviderAuthenticationHandler owns the live credential so rotation
            // does not require recreating the Minimal client.
            ApiKey = null,
            BaseUrl = baseUrl.TrimEnd('/'),
            HttpClient = providerHttpClient,
            Timeout = TimeSpan.FromMinutes(10)
        };
        _ownedClient = new AnthropicMinimalClient(options);
        // Files API 不接受响应指定的下载地址，也不跟随重定向转发凭据。
        _fileBaseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        _fileCredentialHandler = new ProviderAuthenticationHandler("x-api-key", null, apiKey,
            new SocketsHttpHandler { AllowAutoRedirect = false });
        _fileClient = new HttpClient(_fileCredentialHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _chatClientFactory = modelId => _ownedClient.AsIChatClient(modelId);
        ProviderId = providerId;
        Models = models ?? [];
    }

    public string ProviderId { get; }

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        ToolCalling: true,
        Vision: true,
        StructuredOutput: true,
        Reasoning: true,
        Files: true,
        Usage: true,
        RemoteCancellation: false);

    public IReadOnlyList<ProviderModel> Models { get; }

    public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default)
    {
        return ProviderCapabilityValidator.ValidateAsync(Capabilities, input, ct);
    }

    public Task UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(parameters.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Credential update targets another Provider.", nameof(parameters));
        }

        if (_credentialHandler is null)
        {
            throw new InvalidOperationException(
                "Credentials are owned by the injected Anthropic client and cannot be updated here.");
        }

        _credentialHandler.Update(parameters.Credential);
        _fileCredentialHandler?.Update(parameters.Credential);
        return Task.CompletedTask;
    }

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

        RuntimeError? failure = null;
        try
        {
            await ProviderCapabilityValidator.ValidateAsync(
                Capabilities,
                request,
                Models,
                effectiveCt).ConfigureAwait(false);
        }
        catch (RuntimeProviderException ex)
        {
            failure = ex.Error;
        }

        if (failure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
            yield break;
        }

        Signatures.Touch(request.ProviderId, request.Messages);
        Signatures.Sweep();
        var draft = new ReasoningSignatureDraft(appendFragments: true);

        foreach (var adjustment in GetProjectionAdjustments(request))
        {
            yield return adjustment;
        }

        // 记录开始时间和已收到的更新条数，失败时写入 ProviderDetails，用来区分断流、解析失败和请求失败。
        var startedAt = Stopwatch.GetTimestamp();
        var receivedUpdates = 0;
        IAsyncEnumerator<MafProviderResponseUpdate>? enumerator = null;
        try
        {
            var messages = await ConvertMessagesAsync(
                request.ProviderId,
                request.Messages,
                request.Tools,
                effectiveCt).ConfigureAwait(false);
            enumerator = CreateResponseStream(request, messages, effectiveCt).GetAsyncEnumerator(effectiveCt);
        }
        catch (OperationCanceledException) when (effectiveCt.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = CreateProviderError(
                ex,
                ProviderFailureContext.Since(ProviderFailureStages.RequestStart, receivedUpdates, startedAt, request.UseNonStreamingResponse));
        }

        if (failure is not null || enumerator is null)
        {
            yield return new InvocationFailedProviderEvent(
                request.InvocationId,
                failure ?? CreateProviderError(new InvalidOperationException("Provider stream was not created.")));
            yield break;
        }

        string? finishReason = null;
        UsageUpdatedProviderEvent? latestUsage = null;
        await using var responseEnumerator = enumerator;

        while (true)
        {
            MafProviderResponseUpdate update;
            try
            {
                if (!await responseEnumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                update = responseEnumerator.Current;
            }
            catch (OperationCanceledException) when (effectiveCt.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = CreateProviderError(
                    ex,
                    ProviderFailureContext.Since(ProviderFailureStages.StreamRead, receivedUpdates, startedAt, request.UseNonStreamingResponse));
                break;
            }

            receivedUpdates++;
            if (update.FinishReason is { } currentFinishReason)
            {
                finishReason = currentFinishReason.ToString();
            }

            foreach (var providerEvent in MapUpdate(request, update, draft))
            {
                if (providerEvent is UsageUpdatedProviderEvent usage)
                {
                    latestUsage = usage;
                    continue;
                }

                yield return providerEvent;
            }
        }

        if (failure is null && finishReason is null)
        {
            failure = ProviderErrorFactory.Create(
                "Anthropic",
                new InvalidDataException("Provider stream ended before a terminal update."),
                isProtocolError: true,
                failureContext: ProviderFailureContext.Since(
                    ProviderFailureStages.StreamEndedWithoutFinishReason,
                    receivedUpdates,
                    startedAt,
                    request.UseNonStreamingResponse));
        }

        if (failure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
            yield break;
        }

        draft.Commit(Signatures, request.ProviderId);
        Signatures.Sweep();

        yield return latestUsage
            ?? new UsageUpdatedProviderEvent(
                request.InvocationId,
                InputTokens: null,
                OutputTokens: null,
                ProviderUsageAccuracy.Unknown);

        yield return new InvocationCompletedProviderEvent(request.InvocationId, finishReason!);
    }

    private IAsyncEnumerable<MafProviderResponseUpdate> CreateResponseStream(
        RuntimeProviderRequest request,
        ChatMessage[] messages,
        CancellationToken ct)
    {
        var chatClient = _chatClients.GetOrAdd(
            request.ModelId,
            modelId => _chatClientFactory(modelId)
                ?? throw new InvalidOperationException("The Anthropic chat client factory returned null."));
        var options = CreateChatOptions(request);
        return MafSkillsAgentFactory.CreateResponseStream(
            new Protocol.AnthropicFileOutputChatClient(chatClient, _fileClient, _fileBaseUri,
                request.MaximumBufferedBlobBytes),
            request,
            messages,
            options,
            ct);
    }

    private static ChatOptions CreateChatOptions(RuntimeProviderRequest request)
    {
        var options = new ChatOptions
        {
            ModelId = request.ModelId,
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxTokens,
            Tools = CreateTools(request.Tools),
            ResponseFormat = CreateResponseFormat(request.StructuredOutput)
        };

        // Central ApplyRequestControl (cross-checked against latest Anthropic Messages API):
        // Supported: temperature, top_p, top_k, max_tokens, stop_sequences
        // Unsupported (will be ignored by Anthropic): frequency_penalty, presence_penalty, seed
        MafSkillsAgentFactory.ApplyRequestControl(options, request.RequestControl);

        // Preserve metadata for tracing if present
        if (request.RequestControl?.Metadata is { Count: > 0 } metadata)
        {
            options.AdditionalProperties ??= new();
            foreach (var (key, value) in metadata)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    options.AdditionalProperties[$"metadata.{key}"] = value;
                }
            }
        }

        return options;
    }

    private static ChatResponseFormatJson? CreateResponseFormat(RuntimeStructuredOutput? structuredOutput)
    {
        return structuredOutput is null
            ? null
            : ChatResponseFormat.ForJsonSchema(
                structuredOutput.Schema,
                structuredOutput.Name,
                structuredOutput.Description);
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

    private async ValueTask<ChatMessage[]> ConvertMessagesAsync(
        string providerId,
        RuntimeProviderMessage[] source,
        ToolDescriptor[]? tools,
        CancellationToken ct)
    {
        var messages = new List<ChatMessage>(source.Length);

        foreach (var message in source)
        {
            ct.ThrowIfCancellationRequested();
            var content = new List<AIContent>(message.Content.Length);
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextContentBlock text:
                        content.Add(new TextContent(text.Text));
                        break;
                    case ReasoningContentBlock reasoning:
                        content.Add(new TextReasoningContent(reasoning.Content)
                        {
                            ProtectedData = Signatures.TryGet(providerId, reasoning.Content)
                        });
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
                    case BlobRefContentBlock blobReference:
                        content.Add(await ResolveBlobAsync(blobReference.Blob, ct).ConfigureAwait(false));
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

    private ValueTask<DataContent> ResolveBlobAsync(BlobReference blob, CancellationToken ct) =>
        ProviderBlobContentResolver.ResolveAsync(_blobResolver, blob, ProviderAttachmentRoute.AnthropicMessages, ct);

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

    private static IEnumerable<RuntimeProviderEvent> MapUpdate(
        RuntimeProviderRequest request,
        MafProviderResponseUpdate update,
        ReasoningSignatureDraft draft)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning:
                    var reasoningText = draft.Add(reasoning.Text, reasoning.ProtectedData);
                    if (reasoningText is not null)
                    {
                        yield return new ReasoningDeltaProviderEvent(request.InvocationId, reasoningText);
                    }

                    break;

                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    draft.EndBlock();
                    yield return new TextDeltaProviderEvent(request.InvocationId, text.Text);
                    break;

                case ProviderOutputAttachmentContent attachment:
                    draft.EndBlock();
                    yield return new AttachmentCompletedProviderEvent(request.InvocationId,
                        attachment.Blob, attachment.FileName);
                    break;

                case FunctionCallContent functionCall:
                    draft.EndBlock();
                    var callId = functionCall.CallId ?? string.Empty;
                    var name = functionCall.Name ?? string.Empty;
                    var toolId = ResolveToolId(request.Tools, name);

                    if (TryGetPartialJson(functionCall.Arguments, out var partialJson))
                    {
                        yield return new ToolCallDeltaProviderEvent(
                            request.InvocationId,
                            callId,
                            name,
                            partialJson);
                    }
                    else
                    {
                        yield return new ToolCallCompleteProviderEvent(
                            request.InvocationId,
                            callId,
                            toolId,
                            name,
                            SerializeArguments(functionCall.Arguments));
                    }
                    break;

                case UsageContent
                {
                    Details.InputTokenCount: { } inputTokens,
                    Details.OutputTokenCount: { } outputTokens
                }:
                    yield return new UsageUpdatedProviderEvent(
                        request.InvocationId,
                        ClampTokenCount(inputTokens),
                        ClampTokenCount(outputTokens));
                    break;
            }
        }
    }

    private static IEnumerable<ProjectionAdjustedProviderEvent> GetProjectionAdjustments(
        RuntimeProviderRequest request)
    {
        var supportedNamespace = ProviderExtensionPolicy.GetNamespace("anthropic");
        foreach (var reasoning in request.Messages
                     .SelectMany(static message => message.Content)
                     .OfType<ReasoningContentBlock>())
        {
            ProviderExtensionData.ValidateCollection(reasoning.ProviderExtensions);
            if (reasoning.ProviderExtensions is null)
            {
                continue;
            }

            foreach (var extension in reasoning.ProviderExtensions)
            {
                if (string.Equals(extension.Namespace, supportedNamespace, StringComparison.Ordinal)
                    && string.Equals(
                        extension.Name,
                        ProviderExtensionPolicy.ReasoningSignatureName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                yield return new ProjectionAdjustedProviderEvent(
                    request.InvocationId,
                    "provider_extension_omitted",
                    extension.Namespace,
                    extension.Name);
            }
        }
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

    private static bool TryGetPartialJson(
        IDictionary<string, object?>? arguments,
        out string partialJson)
    {
        if (arguments is not null
            && arguments.Count == 1
            && arguments.TryGetValue("partial_json", out var value)
            && value is string partial)
        {
            partialJson = partial;
            return true;
        }

        partialJson = string.Empty;
        return false;
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

    private static int ClampTokenCount(long value)
    {
        return (int)Math.Clamp(value, 0, int.MaxValue);
    }

    /// <summary>把 SDK 异常转换为脱敏的 Runtime 错误，并附带可选的失败阶段上下文。</summary>
    private static RuntimeError CreateProviderError(
        Exception exception,
        ProviderFailureContext? failureContext = null)
    {
        if (exception is RuntimeProviderException runtimeException)
        {
            return runtimeException.Error;
        }

        var statusCode = exception switch
        {
            AnthropicMinimalException anthropicException when anthropicException.StatusCode > 0 =>
                anthropicException.StatusCode,
            HttpRequestException { StatusCode: { } httpStatus } => (int)httpStatus,
            _ => (int?)null
        };

        var providerCode = exception is AnthropicMinimalException minimalException
            ? minimalException.ErrorType
            : null;
        return ProviderErrorFactory.Create(
            "Anthropic",
            exception,
            statusCode,
            requestId: exception is AnthropicMinimalException error
                ? error.RequestId
                : null,
            isProtocolError: exception is JsonException
                || exception is AnthropicMinimalException { StatusCode: 0 },
            providerCode: providerCode,
            failureContext: failureContext);
    }

    public void Dispose()
    {
        _fileClient?.Dispose();
        _ownedClient?.Dispose();
    }
}
