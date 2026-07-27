namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Describes compatibility repairs applied to a Provider history projection.</summary>
public sealed record ProviderRequestCompatibilityDiagnostics(
    int FilteredContentBlockCount,
    int OrphanToolResultCount,
    int DuplicateToolResultCount,
    int MissingToolResultCount,
    int NonAdjacentToolResultCount,
    int DowngradedAssistantMessageCount,
    int DroppedMessageCount)
{
    /// <summary>Gets whether the projection changed any historical content.</summary>
    public bool IsAdjusted =>
        FilteredContentBlockCount > 0
        || OrphanToolResultCount > 0
        || DuplicateToolResultCount > 0
        || MissingToolResultCount > 0
        || NonAdjacentToolResultCount > 0
        || DowngradedAssistantMessageCount > 0
        || DroppedMessageCount > 0;
}
