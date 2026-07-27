using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Creates Provider-compatible request clones without changing canonical history.</summary>
public static class ProviderRequestCompatibilityProjector
{
    private const int MaxDowngradedToolNames = 8;
    private const int MaxDowngradedToolNameLength = 64;

    /// <summary>Projects a request using the effective capabilities exposed by an adapter.</summary>
    public static RuntimeProviderRequest Project(
        RuntimeProviderRequest request,
        IRuntimeProviderAdapter adapter) =>
        ProjectWithDiagnostics(request, adapter).Request;

    /// <summary>Projects a request using explicit Provider and model capabilities.</summary>
    public static RuntimeProviderRequest Project(
        RuntimeProviderRequest request,
        ProviderCapabilities providerCapabilities,
        IReadOnlyList<ProviderModel>? models = null) =>
        ProjectWithDiagnostics(request, providerCapabilities, models).Request;

    /// <summary>Projects a request and reports every compatibility repair.</summary>
    public static ProviderRequestCompatibilityProjection ProjectWithDiagnostics(
        RuntimeProviderRequest request,
        IRuntimeProviderAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return ProjectWithDiagnostics(request, adapter.Capabilities, adapter.Models);
    }

    /// <summary>Projects a request and reports every compatibility repair.</summary>
    public static ProviderRequestCompatibilityProjection ProjectWithDiagnostics(
        RuntimeProviderRequest request,
        ProviderCapabilities providerCapabilities,
        IReadOnlyList<ProviderModel>? models = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        models ??= [];
        if (request.HistoryMessageCount < 0
            || request.HistoryMessageCount > request.Messages.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.HistoryMessageCount,
                "HistoryMessageCount must identify a leading message prefix.");
        }

        var capabilities = ProviderCapabilityValidator.GetEffectiveCapabilities(
            providerCapabilities,
            request.ModelId,
            models);
        ValidateCurrentInvocation(request, capabilities);
        if (request.HistoryMessageCount == 0)
        {
            return new ProviderRequestCompatibilityProjection(
                request,
                new ProviderRequestCompatibilityDiagnostics(0, 0, 0, 0, 0, 0, 0));
        }

        var diagnostics = new DiagnosticsBuilder();
        var history = request.Messages[..request.HistoryMessageCount];
        var projectedHistory = ProjectHistory(history, capabilities, diagnostics);
        var projectedMessages = new RuntimeProviderMessage[
            projectedHistory.Count + request.Messages.Length - request.HistoryMessageCount];
        projectedHistory.CopyTo(projectedMessages, 0);
        Array.Copy(
            request.Messages,
            request.HistoryMessageCount,
            projectedMessages,
            projectedHistory.Count,
            request.Messages.Length - request.HistoryMessageCount);
        var projectedRequest = request with
        {
            Messages = projectedMessages,
            HistoryMessageCount = projectedHistory.Count
        };
        return new ProviderRequestCompatibilityProjection(
            diagnostics.IsAdjusted ? projectedRequest : request,
            diagnostics.ToDiagnostics());
    }

    private static List<RuntimeProviderMessage> ProjectHistory(
        RuntimeProviderMessage[] history,
        ProviderCapabilities capabilities,
        DiagnosticsBuilder diagnostics)
    {
        var results = IndexToolResults(history, capabilities, diagnostics);
        var consumedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<RuntimeProviderMessage>(history.Length);
        for (var messageIndex = 0; messageIndex < history.Length; messageIndex++)
        {
            var message = history[messageIndex];
            if (string.Equals(message.Role, RuntimeProviderRoles.Tool, StringComparison.Ordinal))
            {
                continue;
            }

            var filteredContent = FilterContent(
                message.Content,
                capabilities,
                preserveToolProtocol: true,
                diagnostics);
            var calls = filteredContent.OfType<ToolCallContentBlock>().ToArray();
            if (calls.Length == 0)
            {
                var ordinaryContent = filteredContent
                    .Where(static block => block is not ToolResultContentBlock)
                    .ToArray();
                AddIfNotEmpty(output, message.Role, ordinaryContent, diagnostics);
                continue;
            }

            if (!capabilities.ToolCalling
                || !HasValidCallIds(calls)
                || calls.Any(call => !results.ContainsKey(call.CallId)))
            {
                diagnostics.MissingToolResultCount += calls.Count(call =>
                    string.IsNullOrWhiteSpace(call.CallId)
                    || !results.ContainsKey(call.CallId));
                diagnostics.DowngradedAssistantMessageCount++;
                AddIfNotEmpty(
                    output,
                    RuntimeProviderRoles.Assistant,
                    DowngradeAssistant(filteredContent, calls),
                    diagnostics);
                continue;
            }

            output.Add(message with { Content = filteredContent });
            for (var callIndex = 0; callIndex < calls.Length; callIndex++)
            {
                var call = calls[callIndex];
                var indexedResult = results[call.CallId];
                consumedCallIds.Add(call.CallId);
                if (indexedResult.MessageIndex != messageIndex + callIndex + 1)
                {
                    diagnostics.NonAdjacentToolResultCount++;
                }

                output.Add(new RuntimeProviderMessage(
                    RuntimeProviderRoles.Tool,
                    [indexedResult.Result]));
            }
        }

        diagnostics.OrphanToolResultCount += results.Keys.Count(callId =>
            !consumedCallIds.Contains(callId));
        return output;
    }

    private static Dictionary<string, IndexedToolResult> IndexToolResults(
        RuntimeProviderMessage[] history,
        ProviderCapabilities capabilities,
        DiagnosticsBuilder diagnostics)
    {
        var results = new Dictionary<string, IndexedToolResult>(StringComparer.Ordinal);
        for (var messageIndex = 0; messageIndex < history.Length; messageIndex++)
        {
            var message = history[messageIndex];
            if (!string.Equals(message.Role, RuntimeProviderRoles.Tool, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var result in message.Content.OfType<ToolResultContentBlock>())
            {
                if (string.IsNullOrWhiteSpace(result.CallId))
                {
                    diagnostics.OrphanToolResultCount++;
                    continue;
                }

                var filteredContent = FilterContent(
                    result.Content,
                    capabilities,
                    preserveToolProtocol: capabilities.ToolCalling,
                    diagnostics);
                var projectedResult = result with { Content = filteredContent };
                if (!results.TryAdd(
                        result.CallId,
                        new IndexedToolResult(messageIndex, projectedResult)))
                {
                    diagnostics.DuplicateToolResultCount++;
                }
            }
        }

        return results;
    }

    private static ContentBlock[] FilterContent(
        ContentBlock[] content,
        ProviderCapabilities capabilities,
        bool preserveToolProtocol,
        DiagnosticsBuilder diagnostics)
    {
        var output = new List<ContentBlock>(content.Length);
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContentBlock:
                    output.Add(block);
                    break;
                case ReasoningContentBlock when capabilities.Reasoning:
                    output.Add(block);
                    break;
                case ReasoningContentBlock:
                    diagnostics.FilteredContentBlockCount++;
                    break;
                case BlobRefContentBlock blob when IsBlobSupported(capabilities, blob.Blob.ContentType):
                    output.Add(block);
                    break;
                case BlobRefContentBlock:
                    diagnostics.FilteredContentBlockCount++;
                    break;
                case ToolCallContentBlock when preserveToolProtocol:
                    output.Add(block);
                    break;
                case ToolCallContentBlock:
                    diagnostics.FilteredContentBlockCount++;
                    break;
                case ToolResultContentBlock result when preserveToolProtocol:
                    output.Add(result with
                    {
                        Content = FilterContent(
                            result.Content,
                            capabilities,
                            preserveToolProtocol,
                            diagnostics)
                    });
                    break;
                case ToolResultContentBlock:
                    diagnostics.FilteredContentBlockCount++;
                    break;
                default:
                    diagnostics.FilteredContentBlockCount++;
                    break;
            }
        }

        return [.. output];
    }

    private static ContentBlock[] DowngradeAssistant(
        ContentBlock[] content,
        ToolCallContentBlock[] calls)
    {
        var output = content.OfType<TextContentBlock>()
            .Cast<ContentBlock>()
            .ToList();
        var names = calls
            .Select(static call => call.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Length <= MaxDowngradedToolNameLength
                ? name
                : name[..MaxDowngradedToolNameLength])
            .Distinct(StringComparer.Ordinal)
            .Take(MaxDowngradedToolNames)
            .ToArray();
        var summary = names.Length == 0
            ? "[Historical tool call omitted.]"
            : $"[Historical tool calls omitted: {string.Join(", ", names)}]";
        output.Add(new TextContentBlock(summary));
        return [.. output];
    }

    private static bool HasValidCallIds(ToolCallContentBlock[] calls)
    {
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        return calls.All(call =>
            !string.IsNullOrWhiteSpace(call.CallId)
            && callIds.Add(call.CallId));
    }

    private static void AddIfNotEmpty(
        List<RuntimeProviderMessage> output,
        string role,
        ContentBlock[] content,
        DiagnosticsBuilder diagnostics)
    {
        if (content.Length == 0)
        {
            diagnostics.DroppedMessageCount++;
            return;
        }

        output.Add(new RuntimeProviderMessage(role, content));
    }

    private static void ValidateCurrentInvocation(
        RuntimeProviderRequest request,
        ProviderCapabilities capabilities)
    {
        for (var index = request.HistoryMessageCount; index < request.Messages.Length; index++)
        {
            ValidateCurrentContent(request.Messages[index].Content, capabilities);
        }
    }

    private static void ValidateCurrentContent(
        ContentBlock[] content,
        ProviderCapabilities capabilities)
    {
        foreach (var block in content)
        {
            var unsupportedCapability = block switch
            {
                ReasoningContentBlock when !capabilities.Reasoning => "reasoning",
                ToolCallContentBlock or ToolResultContentBlock when !capabilities.ToolCalling =>
                    "tool_calling",
                BlobRefContentBlock blob when !IsBlobSupported(
                    capabilities,
                    blob.Blob.ContentType) => GetBlobCapability(blob.Blob.ContentType),
                _ => null
            };
            if (unsupportedCapability is not null)
            {
                throw CreateCapabilityException(unsupportedCapability);
            }

            if (block is ToolResultContentBlock result)
            {
                ValidateCurrentContent(result.Content, capabilities);
            }
        }
    }

    private static bool IsBlobSupported(
        ProviderCapabilities capabilities,
        string contentType) =>
        GetBlobCapability(contentType) switch
        {
            "vision" => capabilities.Vision,
            "audio" => capabilities.Audio,
            _ => capabilities.Files
        };

    private static string GetBlobCapability(string contentType)
    {
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return "vision";
        }

        return contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            ? "audio"
            : "files";
    }

    private static RuntimeProviderException CreateCapabilityException(string capability) =>
        new(new RuntimeError(
            RuntimeErrorCodes.CapabilityNotSupported,
            "capability",
            $"Provider capability '{capability}' is not supported for this request.",
            IsRetryable: false,
            ProviderDetails: null,
            DiagnosticId: Guid.NewGuid().ToString("N")));

    private sealed record IndexedToolResult(
        int MessageIndex,
        ToolResultContentBlock Result);

    private sealed class DiagnosticsBuilder
    {
        public int FilteredContentBlockCount { get; set; }

        public int OrphanToolResultCount { get; set; }

        public int DuplicateToolResultCount { get; set; }

        public int MissingToolResultCount { get; set; }

        public int NonAdjacentToolResultCount { get; set; }

        public int DowngradedAssistantMessageCount { get; set; }

        public int DroppedMessageCount { get; set; }

        public bool IsAdjusted => ToDiagnostics().IsAdjusted;

        public ProviderRequestCompatibilityDiagnostics ToDiagnostics() =>
            new(
                FilteredContentBlockCount,
                OrphanToolResultCount,
                DuplicateToolResultCount,
                MissingToolResultCount,
                NonAdjacentToolResultCount,
                DowngradedAssistantMessageCount,
                DroppedMessageCount);
    }
}
