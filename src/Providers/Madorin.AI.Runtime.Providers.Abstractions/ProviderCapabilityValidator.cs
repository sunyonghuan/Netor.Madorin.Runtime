using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

public static class ProviderCapabilityValidator
{
    public static Task ValidateAsync(
        ProviderCapabilities providerCapabilities,
        RuntimeProviderRequest request,
        IReadOnlyList<ProviderModel> models,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(models);
        ct.ThrowIfCancellationRequested();

        var capabilities = GetEffectiveCapabilities(
            providerCapabilities,
            request.ModelId,
            models);

        ValidateSkillsSupport(capabilities, request.Skills, ct);

        if (request.Tools is { Length: > 0 } && !capabilities.ToolCalling)
        {
            throw CreateCapabilityException("tool_calling");
        }

        if (request.StructuredOutput is not null && !capabilities.StructuredOutput)
        {
            throw CreateCapabilityException("structured_output");
        }

        return ValidateAsync(
            capabilities,
            [.. request.Messages.SelectMany(static message => message.Content)],
            ct);
    }

    public static void ValidateSkillsSupport(
        ProviderCapabilities capabilities,
        SkillsInvocationContext? skills,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (skills is not null && !capabilities.ToolCalling)
        {
            throw CreateSkillsUnsupportedException();
        }
    }

    public static Task ValidateAsync(
        ProviderCapabilities capabilities,
        ContentBlock[] input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        foreach (var block in Flatten(input))
        {
            ct.ThrowIfCancellationRequested();

            var unsupportedCapability = block switch
            {
                ReasoningContentBlock when !capabilities.Reasoning => "reasoning",
                ToolCallContentBlock or ToolResultContentBlock when !capabilities.ToolCalling => "tool_calling",
                BlobRefContentBlock blob => GetUnsupportedBlobCapability(
                    capabilities,
                    blob.Blob.ContentType),
                _ => null
            };

            if (unsupportedCapability is not null)
            {
                throw CreateCapabilityException(unsupportedCapability);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Combines Provider-level and model-level capabilities for one request.</summary>
    public static ProviderCapabilities GetEffectiveCapabilities(
        ProviderCapabilities providerCapabilities,
        string modelId,
        IReadOnlyList<ProviderModel> models)
    {
        var model = models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.Ordinal)
            || string.Equals(candidate.ProviderModelId, modelId, StringComparison.Ordinal));
        var modelCapabilities = model?.Capabilities;
        if (modelCapabilities is null)
        {
            return providerCapabilities;
        }

        return new ProviderCapabilities(
            Streaming: providerCapabilities.Streaming && modelCapabilities.Streaming,
            ToolCalling: providerCapabilities.ToolCalling && modelCapabilities.ToolCalling,
            Vision: providerCapabilities.Vision && modelCapabilities.Vision,
            Audio: providerCapabilities.Audio && modelCapabilities.Audio,
            StructuredOutput: providerCapabilities.StructuredOutput && modelCapabilities.StructuredOutput,
            Reasoning: providerCapabilities.Reasoning && modelCapabilities.Reasoning,
            PromptCaching: providerCapabilities.PromptCaching && modelCapabilities.PromptCaching,
            Files: providerCapabilities.Files && modelCapabilities.Files,
            ComputerUse: providerCapabilities.ComputerUse && modelCapabilities.ComputerUse,
            Embeddings: providerCapabilities.Embeddings && modelCapabilities.Embeddings,
            Usage: providerCapabilities.Usage && modelCapabilities.Usage,
            RemoteCancellation: providerCapabilities.RemoteCancellation && modelCapabilities.RemoteCancellation);
    }

    private static IEnumerable<ContentBlock> Flatten(IEnumerable<ContentBlock> input)
    {
        foreach (var block in input)
        {
            yield return block;
            if (block is ToolResultContentBlock result)
            {
                foreach (var nested in Flatten(result.Content))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string? GetUnsupportedBlobCapability(
        ProviderCapabilities capabilities,
        string contentType)
    {
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return capabilities.Vision ? null : "vision";
        }

        if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return capabilities.Audio ? null : "audio";
        }

        return capabilities.Files ? null : "files";
    }

    private static RuntimeProviderException CreateCapabilityException(string capability)
    {
        var error = new RuntimeError(
            RuntimeErrorCodes.CapabilityNotSupported,
            "capability",
            $"Provider capability '{capability}' is not supported for this request.",
            IsRetryable: false,
            ProviderDetails: null,
            DiagnosticId: Guid.NewGuid().ToString("N"));

        return new RuntimeProviderException(error);
    }

    private static RuntimeProviderException CreateSkillsUnsupportedException()
    {
        var error = new RuntimeError(
            RuntimeErrorCodes.SkillsNotSupportedByProvider,
            "capability",
            "The selected Provider does not support Skills because Tool Calling is unavailable.",
            IsRetryable: false,
            ProviderDetails: null,
            DiagnosticId: Guid.NewGuid().ToString("N"));
        return new RuntimeProviderException(error);
    }
}
