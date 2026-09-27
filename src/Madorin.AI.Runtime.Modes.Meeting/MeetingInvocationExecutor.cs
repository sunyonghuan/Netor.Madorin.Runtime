using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Orchestration.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Modes.Meeting;

internal sealed class MeetingInvocationExecutor(
    IAgentInvocationExecutor providerExecutor,
    ConversationStore store,
    ToolGateway? toolGateway,
    ToolCatalogSnapshot? toolCatalogSnapshot,
    RuntimeLimits? runtimeLimits,
    ToolConsentCoordinator? toolConsentCoordinator,
    Func<ToolCatalogSnapshot>? liveCatalogProvider = null,
    Func<MeetingHitlRequest, CancellationToken, Task<MeetingHitlResponse>>? askUserHitlHandler = null)
{
    private const string AskUserToolId = "mcli_ask_user";

    private readonly IAgentInvocationExecutor _providerExecutor = providerExecutor
        ?? throw new ArgumentNullException(nameof(providerExecutor));
    private readonly ConversationStore _store = store
        ?? throw new ArgumentNullException(nameof(store));
    private readonly ToolGateway? _toolGateway = toolGateway;
    private readonly ToolCatalogSnapshot? _toolCatalogSnapshot = toolCatalogSnapshot;
    private readonly Func<ToolCatalogSnapshot>? _liveCatalogProvider = liveCatalogProvider;
    private readonly ToolConsentCoordinator? _toolConsentCoordinator = toolConsentCoordinator;
    private readonly Func<MeetingHitlRequest, CancellationToken, Task<MeetingHitlResponse>>? _askUserHitlHandler =
        askUserHitlHandler;
    private readonly RuntimeLimits _runtimeLimits = runtimeLimits ?? new RuntimeLimits(
        MaxToolResultBytesPerInvocation: 4 * 1024 * 1024);

    public async Task<MeetingInvocationExecutionResult> ExecuteAsync(
        AgentInvocationRequest request,
        int maxRetries,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        ValidateToolCallLimit(_runtimeLimits.MaxToolCallsPerRound);

        var providerEvents = new List<RuntimeProviderEvent>();
        var currentRequest = request;
        var maxToolRounds = _runtimeLimits.MaxToolRounds;
        var maxToolCallsPerRound = _runtimeLimits.MaxToolCallsPerRound;
        var maxToolResultBytes = _runtimeLimits.MaxToolResultBytesPerInvocation
            ?? 4L * 1024 * 1024;
        long totalToolResultBytes = 0;

        for (var toolRound = 0; ; toolRound++)
        {
            var providerRound = await ExecuteProviderRoundAsync(
                currentRequest,
                currentRequest.HasIrreversibleToolSideEffects ? 0 : maxRetries,
                ct).ConfigureAwait(false);
            providerEvents.AddRange(providerRound.Events);
            if (!providerRound.Succeeded)
            {
                return new MeetingInvocationExecutionResult(
                    false,
                    null,
                    providerRound.Error,
                    providerEvents);
            }

            var toolCalls = providerRound.Content.OfType<ToolCallContentBlock>().ToArray();
            if (toolCalls.Length == 0)
            {
                return new MeetingInvocationExecutionResult(
                    true,
                    string.Concat(providerRound.Content
                        .OfType<TextContentBlock>()
                        .Select(static block => block.Text)),
                    null,
                    providerEvents);
            }

            if (maxToolRounds is { } toolRoundLimit && toolRound >= toolRoundLimit)
            {
                return Failed(
                    RuntimeErrorCodes.LimitsIncompatible,
                    $"The Invocation exceeded the {toolRoundLimit}-round tool limit.",
                    providerEvents);
            }

            if (maxToolCallsPerRound is { } toolCallLimit
                && toolCalls.Length > toolCallLimit)
            {
                return Failed(
                    RuntimeErrorCodes.LimitsIncompatible,
                    $"The Provider requested {toolCalls.Length} tools in one round; the limit is {toolCallLimit}.",
                    providerEvents);
            }

            if (toolCalls.Select(static call => call.CallId)
                .Distinct(StringComparer.Ordinal).Count() != toolCalls.Length)
            {
                return Failed(
                    RuntimeErrorCodes.ToolCallConflict,
                    "The Provider returned duplicate tool call ids in one round.",
                    providerEvents);
            }

            var liveCatalog = CaptureCatalogSnapshot();
            if (_toolGateway is null
                || liveCatalog is null
                || currentRequest.AvailableTools is null)
            {
                return Failed(
                    RuntimeErrorCodes.ToolExecutorUnavailable,
                    "The meeting participant does not have an available tool gateway.",
                    providerEvents);
            }

            var allowedToolIds = currentRequest.AvailableTools
                .Select(static tool => tool.ToolId)
                .ToHashSet(StringComparer.Ordinal);
            if (toolCalls.Any(call => !allowedToolIds.Contains(call.ToolId)))
            {
                return Failed(
                    RuntimeErrorCodes.ToolPermissionRequired,
                    "The Provider requested a tool outside the participant's allowed tool set.",
                    providerEvents);
            }

            await PersistContentAsync(
                currentRequest,
                RuntimeProviderRoles.Assistant,
                providerRound.Content,
                CreateStableId(
                    "meeting-tool-assistant-v1",
                    request.InvocationId,
                    toolRound.ToString(CultureInfo.InvariantCulture)),
                ct).ConfigureAwait(false);

            var continuationMessages = currentRequest.Messages.ToList();
            continuationMessages.Add(new RuntimeProviderMessage(
                RuntimeProviderRoles.Assistant,
                providerRound.Content));
            foreach (var toolCall in toolCalls)
            {
                var invocation = new ToolInvocation(
                    toolCall.CallId,
                    toolCall.ToolId,
                    request.AgentId,
                    ParentAgentId: null,
                    toolCall.Arguments.GetRawText(),
                    request.RunId,
                    request.SessionId,
                    request.InvocationId,
                    ToolCatalogVersion: liveCatalog.EffectiveVersion);
                ToolGatewayResult gatewayResult;
                try
                {
                    var callCatalog = CaptureCatalogSnapshot() ?? liveCatalog;
                    gatewayResult = await _toolGateway.ExecuteAsync(
                        invocation,
                        permission: null,
                        callCatalog,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }

                if (gatewayResult.Kind is ToolGatewayResultKind.NeedsApproval
                    or ToolGatewayResultKind.NeedsPermission)
                {
                    if (_toolConsentCoordinator is null)
                    {
                        return Failed(
                            gatewayResult.Error?.Code ?? RuntimeErrorCodes.ToolPermissionRequired,
                            gatewayResult.Error?.Message
                                ?? "The meeting tool call requires host consent.",
                            providerEvents);
                    }

                    gatewayResult = await _toolConsentCoordinator.ResolveAndExecuteAsync(
                        invocation,
                        gatewayResult,
                        ct).ConfigureAwait(false);
                }

                var resolvedGatewayResult = gatewayResult;
                if (string.Equals(toolCall.ToolId, AskUserToolId, StringComparison.Ordinal)
                    && gatewayResult.Kind is ToolGatewayResultKind.Success
                    && gatewayResult.Result is { Success: true, OutputJson: { } askOutput }
                    && TryReadWaitingAskUser(askOutput, out var askRequestId, out var question))
                {
                    resolvedGatewayResult = await ResolveAskUserHitlAsync(
                        request,
                        toolCall,
                        gatewayResult.Result,
                        askRequestId,
                        question,
                        ct).ConfigureAwait(false);
                    if (resolvedGatewayResult.Kind is not ToolGatewayResultKind.Success
                        || resolvedGatewayResult.Result is not { Success: true })
                    {
                        return Failed(
                            resolvedGatewayResult.Error?.Code
                                ?? RuntimeErrorCodes.ToolPermissionRequired,
                            resolvedGatewayResult.Error?.Message
                                ?? resolvedGatewayResult.Result?.Error
                                ?? "mcli_ask_user was cancelled or failed while waiting for the user.",
                            providerEvents);
                    }
                }

                var toolResult = CreateToolResultContent(toolCall, resolvedGatewayResult);
                if (request.IsHostModeSkills
                    && resolvedGatewayResult.Kind is ToolGatewayResultKind.Success
                    && resolvedGatewayResult.Result is { Success: true, OutputJson: { } outputJson })
                {
                    HostModeSkillsSnapshot.TryApplyToolResult(
                        request.SkillActivationStore,
                        request.SkillAllowlistIds,
                        toolCall.ToolId,
                        toolCall.Name,
                        outputJson,
                        request.SkillDiscoveryMode);
                }

                totalToolResultBytes += GetContentByteCount(toolResult);
                if (HasOversizedInlineContent(
                        toolResult,
                        _runtimeLimits.MaxInlineContentBytes)
                    || totalToolResultBytes > maxToolResultBytes)
                {
                    return Failed(
                        RuntimeErrorCodes.LimitsIncompatible,
                        "Tool results exceeded the configured inline or Invocation size limit.",
                        providerEvents);
                }

                await PersistContentAsync(
                    currentRequest,
                    RuntimeProviderRoles.Tool,
                    [toolResult],
                    CreateStableId("meeting-tool-result-v1", request.InvocationId, toolCall.CallId),
                    ct).ConfigureAwait(false);
                continuationMessages.Add(new RuntimeProviderMessage(
                    RuntimeProviderRoles.Tool,
                    [toolResult]));
            }

            // search 注入后刷新本轮 AvailableTools（全量 agent 跟随 live；allowlist 只刷新描述符）
            currentRequest = currentRequest with
            {
                Messages = [.. continuationMessages],
                AvailableTools = RefreshExposedTools(currentRequest.AvailableTools),
                IsIdempotent = false,
                HasIrreversibleToolSideEffects = true
            };
        }
    }

    private ToolCatalogSnapshot? CaptureCatalogSnapshot()
    {
        if (_liveCatalogProvider is not null)
        {
            return _liveCatalogProvider();
        }

        return _toolCatalogSnapshot;
    }

    /// <summary>
    /// Full-catalog participants follow live growth after search injection;
    /// allowlist participants only refresh descriptors for already-allowed ToolIds.
    /// </summary>
    private ToolDescriptor[]? RefreshExposedTools(ToolDescriptor[]? previouslyExposed)
    {
        var catalog = CaptureCatalogSnapshot();
        if (_toolGateway is null || catalog is null)
        {
            return previouslyExposed;
        }

        // Meeting 不得暴露 Work 专用工具。
        catalog = catalog.FilterByMode("meeting");

        if (previouslyExposed is null)
        {
            return [.. catalog.Tools];
        }

        var previousIds = previouslyExposed
            .Select(static tool => tool.ToolId)
            .ToHashSet(StringComparer.Ordinal);

        var initial = _toolCatalogSnapshot;
        var wasFullCatalog = initial is not null
            && initial.Tools.Count > 0
            && initial.Tools.All(tool => previousIds.Contains(tool.ToolId))
            && previouslyExposed.Length >= initial.Tools.Count;

        if (wasFullCatalog)
        {
            return [.. catalog.Tools];
        }

        return catalog.Tools
            .Where(tool => previousIds.Contains(tool.ToolId))
            .ToArray();
    }

        private async Task<ProviderRoundResult> ExecuteProviderRoundAsync(
        AgentInvocationRequest request,
        int maxRetries,
        CancellationToken ct)
    {
        RuntimeError? lastError = null;
        var events = new List<RuntimeProviderEvent>();
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            var content = new List<ContentBlock>();
            var completed = false;
            lastError = null;
            try
            {
                await foreach (var providerEvent in _providerExecutor.ExecuteAsync(request, ct)
                    .ConfigureAwait(false))
                {
                    if (!string.Equals(
                        providerEvent.InvocationId,
                        request.InvocationId,
                        StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Provider returned event for invocation '{providerEvent.InvocationId}' but expected '{request.InvocationId}'.");
                    }

                    switch (providerEvent)
                    {
                        case InvocationCompletedProviderEvent:
                            completed = true;
                            break;
                        case InvocationFailedProviderEvent failed:
                            lastError = failed.Error;
                            break;
                        default:
                            events.Add(providerEvent);
                            AccumulateContent(content, providerEvent);
                            break;
                    }

                    if (completed || lastError is not null)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = CreateError(
                    "provider_error",
                    $"Provider threw an unexpected error for invocation '{request.InvocationId}': {ex.Message}");
            }

            if (completed)
            {
                return new ProviderRoundResult(true, [.. content], null, events);
            }

            lastError ??= CreateError(
                "stream_incomplete",
                $"Provider stream ended without a completion event for invocation '{request.InvocationId}'.");
        }

        return new ProviderRoundResult(false, [], lastError, events);
    }

    private async Task PersistContentAsync(
        AgentInvocationRequest request,
        string role,
        ContentBlock[] content,
        string messageId,
        CancellationToken ct)
    {
        var sequence = await _store.GetLastSequenceAsync(request.SessionId, ct)
            .ConfigureAwait(false) + 1;
        var serialized = JsonSerializer.SerializeToElement(
            content,
            RuntimeJsonContext.Default.ContentBlockArray);
        await _store.AppendMessageAsync(
            request.SessionId,
            RuntimeMode.Meeting.ToString(),
            new ConversationRecordV1(
                messageId,
                sequence,
                request.InvocationId,
                request.AgentId,
                role,
                serialized,
                DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);
    }

    private static void AccumulateContent(
        List<ContentBlock> content,
        RuntimeProviderEvent providerEvent)
    {
        switch (providerEvent)
        {
            case TextDeltaProviderEvent text:
                if (content.LastOrDefault() is TextContentBlock previous)
                {
                    content[^1] = previous with { Text = previous.Text + text.Delta };
                }
                else
                {
                    content.Add(new TextContentBlock(text.Delta));
                }

                break;
            case ReasoningDeltaProviderEvent reasoning:
                content.Add(new ReasoningContentBlock(
                    reasoning.Delta,
                    reasoning.ProviderExtensions));
                break;
            case ToolCallCompleteProviderEvent toolCall:
                using (var arguments = JsonDocument.Parse(toolCall.ArgumentsJson))
                {
                    content.Add(new ToolCallContentBlock(
                        toolCall.CallId,
                        toolCall.ToolId,
                        toolCall.Name,
                        arguments.RootElement.Clone()));
                }

                break;
        }
    }

    private static ToolResultContentBlock CreateToolResultContent(
        ToolCallContentBlock toolCall,
        ToolGatewayResult gatewayResult)
    {
        var success = gatewayResult.Kind == ToolGatewayResultKind.Success
            && gatewayResult.Result is { Success: true };
        ContentBlock[] content = success
            ? CreateSuccessfulToolContent(gatewayResult.Result!)
            :
            [
                new TextContentBlock(
                    gatewayResult.Error?.Message
                    ?? gatewayResult.Result?.Error
                    ?? "Tool execution failed.")
            ];
        return new ToolResultContentBlock(
            toolCall.CallId,
            toolCall.ToolId,
            success,
            content);
    }

    private static ContentBlock[] CreateSuccessfulToolContent(ToolResult result)
    {
        var blocks = new List<ContentBlock>(2);
        if (result.OutputJson is { } outputJson)
        {
            blocks.Add(new TextContentBlock(outputJson));
        }

        if (result.ResultBlob is { } resultBlob)
        {
            blocks.Add(new BlobRefContentBlock(resultBlob));
        }

        return [.. blocks];
    }

    private static int GetContentByteCount(ToolResultContentBlock content) =>
        JsonSerializer.SerializeToUtf8Bytes(
            (ContentBlock)content,
            RuntimeJsonContext.Default.ContentBlock).Length;

    private static bool HasOversizedInlineContent(
        ToolResultContentBlock content,
        int maxInlineContentBytes) =>
        content.Content.OfType<TextContentBlock>()
            .Any(text => Encoding.UTF8.GetByteCount(text.Text) > maxInlineContentBytes);

    private static MeetingInvocationExecutionResult Failed(
        string code,
        string message,
        IReadOnlyList<RuntimeProviderEvent> events) =>
        new(false, null, CreateError(code, message), events);

    private static RuntimeError CreateError(string code, string message) =>
        new(
            code,
            "orchestration",
            message,
            IsRetryable: false,
            ProviderDetails: null,
            Guid.NewGuid().ToString("N"));

    private static void ValidateToolCallLimit(int? maxToolCallsPerRound)
    {
        if (maxToolCallsPerRound is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxToolCallsPerRound),
                "MaxToolCallsPerRound must be a positive integer or null.");
        }
    }

    private static string CreateStableId(params string[] components) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", components))))
            .ToLowerInvariant();

    private static string WriteAskUserAnswerJson(
        string requestId,
        string question,
        string answer,
        string action)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("waitingForUser", false);
            writer.WriteString("requestId", requestId);
            writer.WriteString("question", question);
            writer.WriteString("userAnswer", answer);
            writer.WriteString("action", action);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool TryReadWaitingAskUser(
        string outputJson,
        out string requestId,
        out string question)
    {
        requestId = string.Empty;
        question = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(outputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("waitingForUser", out var waiting)
                || waiting.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            question = document.RootElement.TryGetProperty("question", out var questionElement)
                ? questionElement.GetString() ?? string.Empty
                : string.Empty;
            requestId = document.RootElement.TryGetProperty("requestId", out var requestElement)
                ? requestElement.GetString() ?? string.Empty
                : string.Empty;
            return !string.IsNullOrWhiteSpace(question) && !string.IsNullOrWhiteSpace(requestId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<ToolGatewayResult> ResolveAskUserHitlAsync(
        AgentInvocationRequest request,
        ToolCallContentBlock toolCall,
        ToolResult pendingResult,
        string requestId,
        string question,
        CancellationToken ct)
    {
        if (_askUserHitlHandler is null)
        {
            return new ToolGatewayResult(
                ToolGatewayResultKind.Failed,
                Result: new ToolResult(
                    toolCall.CallId,
                    toolCall.ToolId,
                    false,
                    Error: "mcli_ask_user requires a meeting HITL handler."),
                Error: new RuntimeError(
                    RuntimeErrorCodes.ToolPermissionRequired,
                    "orchestration",
                    "mcli_ask_user requires a meeting HITL handler.",
                    IsRetryable: false,
                    ProviderDetails: null,
                    Guid.NewGuid().ToString("N")));
        }

        var roundIndex = request.Snapshot.RoundIndex ?? 0;
        var hitlRequest = new MeetingHitlRequest(
            requestId,
            request.SessionId,
            request.RunId,
            RoundIndex: roundIndex,
            HostInvocationId: request.InvocationId,
            Prompt: "meeting.hitl.question.ask-user",
            Context:
            [
                new TextContentBlock(question)
            ],
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: null);

        MeetingHitlResponse response;
        try
        {
            response = await _askUserHitlHandler(hitlRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        if (response is null
            || !string.Equals(response.ApprovalRequestId, requestId, StringComparison.Ordinal))
        {
            return new ToolGatewayResult(
                ToolGatewayResultKind.Failed,
                Result: new ToolResult(
                    toolCall.CallId,
                    toolCall.ToolId,
                    false,
                    Error: "mcli_ask_user HITL returned a null or mismatched response."),
                Error: new RuntimeError(
                    RuntimeErrorCodes.ToolPermissionRequired,
                    "orchestration",
                    "mcli_ask_user HITL returned a null or mismatched response.",
                    IsRetryable: false,
                    ProviderDetails: null,
                    Guid.NewGuid().ToString("N")));
        }

        switch (response.Action)
        {
            case MeetingHitlAction.Approve:
            case MeetingHitlAction.Supplement:
            {
                var answer = !string.IsNullOrWhiteSpace(response.Supplement)
                    ? response.Supplement
                    : !string.IsNullOrWhiteSpace(response.Reason)
                        ? response.Reason
                        : "approved";
                var answeredJson = WriteAskUserAnswerJson(
                    requestId,
                    question,
                    answer,
                    response.Action.ToString());
                return new ToolGatewayResult(
                    ToolGatewayResultKind.Success,
                    Result: pendingResult with
                    {
                        Success = true,
                        OutputJson = answeredJson,
                        Error = null
                    });
            }

            case MeetingHitlAction.Reject:
            case MeetingHitlAction.Cancel:
            {
                var reason = response.Reason ?? "User rejected or cancelled mcli_ask_user.";
                return new ToolGatewayResult(
                    ToolGatewayResultKind.Failed,
                    Result: new ToolResult(
                        toolCall.CallId,
                        toolCall.ToolId,
                        false,
                        Error: reason),
                    Error: new RuntimeError(
                        "meeting_ask_user_cancelled",
                        "orchestration",
                        reason,
                        IsRetryable: false,
                        ProviderDetails: null,
                        Guid.NewGuid().ToString("N")));
            }

            default:
                return new ToolGatewayResult(
                    ToolGatewayResultKind.Failed,
                    Result: new ToolResult(
                        toolCall.CallId,
                        toolCall.ToolId,
                        false,
                        Error: $"Unknown mcli_ask_user HITL action '{response.Action}'."),
                    Error: new RuntimeError(
                        "meeting_ask_user_invalid",
                        "orchestration",
                        $"Unknown mcli_ask_user HITL action '{response.Action}'.",
                        IsRetryable: false,
                        ProviderDetails: null,
                        Guid.NewGuid().ToString("N")));
        }
    }

    private sealed record ProviderRoundResult(
        bool Succeeded,
        ContentBlock[] Content,
        RuntimeError? Error,
        IReadOnlyList<RuntimeProviderEvent> Events);
}

internal sealed record MeetingInvocationExecutionResult(
    bool Succeeded,
    string? Text,
    RuntimeError? Error,
    IReadOnlyList<RuntimeProviderEvent> Events);
