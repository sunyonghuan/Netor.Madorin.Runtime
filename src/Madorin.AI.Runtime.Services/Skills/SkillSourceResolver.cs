using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Services.Skills;

/// <summary>Builds the deterministic Runtime source order.</summary>
public sealed class SkillSourceResolver
{
#pragma warning disable CA1822 // Instance API is intentional for DI and host composition.
    public IReadOnlyList<SkillSourceDescriptor> Resolve(
        string workspaceRoot,
        IReadOnlyList<SkillSourceDescriptor>? explicitSources = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return SkillCatalogService.ResolveSources(workspaceRoot, explicitSources ?? []);
    }

    public IReadOnlyList<SkillSourceDescriptor> ResolveSources(
        string workspaceRoot,
        IReadOnlyList<SkillSourceDescriptor>? explicitSources = null) =>
        Resolve(workspaceRoot, explicitSources);
#pragma warning restore CA1822
}
