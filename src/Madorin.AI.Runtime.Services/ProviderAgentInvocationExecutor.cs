using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Orchestration.Abstractions;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Services;

/// <summary>
/// Default <see cref="IAgentInvocationExecutor"/> that delegates streaming to a resolved <see cref="IRuntimeProviderAdapter"/>.
/// </summary>
public sealed class ProviderAgentInvocationExecutor : IAgentInvocationExecutor
{
    private readonly Func<string, IRuntimeProviderAdapter> _providerFactory;
    private readonly Func<RuntimeSkillScriptInvocation, CancellationToken, Task<RuntimeSkillScriptResult>>?
        _scriptInvoker;

    public ProviderAgentInvocationExecutor(
        Func<string, IRuntimeProviderAdapter> providerFactory,
        Func<RuntimeSkillScriptInvocation, CancellationToken, Task<RuntimeSkillScriptResult>>? scriptInvoker = null)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _scriptInvoker = scriptInvoker;
    }

    public async IAsyncEnumerable<RuntimeProviderEvent> ExecuteAsync(
        AgentInvocationRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var adapter = _providerFactory(request.ProviderId)
            ?? throw new InvalidOperationException($"Provider adapter not registered for Provider ID '{request.ProviderId}'.");

        var providerRequest = new RuntimeProviderRequest(
            request.InvocationId,
            request.AgentId,
            request.ProviderId,
            request.ModelId,
            request.Messages,
            request.AvailableTools,
            Temperature: null,
            MaxTokens: null,
            StructuredOutput: request.StructuredOutput,
            InternalRequestId: null,
            AttemptNumber: 0,
            IsIdempotent: request.IsIdempotent,
            HasIrreversibleToolSideEffects: request.HasIrreversibleToolSideEffects,
            CancellationToken: ct,
            Skills: CreateSkillsContext(request, _scriptInvoker))
        {
            HistoryMessageCount = request.HistoryMessageCount
        };

        await foreach (var evt in ProviderInvocationBoundary.CompleteStreamingAsync(
                adapter,
                providerRequest,
                ct)
            .ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    private static SkillsInvocationContext? CreateSkillsContext(
        AgentInvocationRequest request,
        Func<RuntimeSkillScriptInvocation, CancellationToken, Task<RuntimeSkillScriptResult>>? scriptInvoker)
    {
        var snapshot = request.Snapshot;
        if (string.IsNullOrWhiteSpace(snapshot.SkillCatalogVersion))
        {
            return null;
        }

        if (request.IsHostModeSkills)
        {
            var loadablePaths = ResolveHostPaths(request);
            return new SkillsInvocationContext(
                snapshot.SkillCatalogVersion,
                snapshot.SkillNames ?? [],
                snapshot.SkillContentHashes ?? [],
                snapshot.SkillSourceIds ?? [],
                loadablePaths,
                scriptInvoker,
                request.RunId,
                request.SessionId,
                request.AgentId,
                request.InvocationId,
                request.ParentAgentId,
                snapshot.ParentInvocationId,
                snapshot.WorkStepId,
                snapshot.WorkPlanVersion,
                IsHostMode: true,
                ActivationStore: request.SkillActivationStore,
                AllowlistSkillIds: request.SkillAllowlistIds,
                CatalogMaterializedPathsByName: request.SkillCatalogPathsByName,
                CatalogDescriptionsByName: request.SkillCatalogDescriptionsByName,
                ScopeSourceRoots: request.SkillScopeSourceRoots,
                SkillDiscoveryMode: request.SkillDiscoveryMode);
        }

        return new SkillsInvocationContext(
            snapshot.SkillCatalogVersion,
            snapshot.SkillNames ?? [],
            snapshot.SkillContentHashes ?? [],
            snapshot.SkillSourceIds ?? [],
            snapshot.SkillMaterializedPaths,
            scriptInvoker,
            request.RunId,
            request.SessionId,
            request.AgentId,
            request.InvocationId,
            request.ParentAgentId,
            snapshot.ParentInvocationId,
            snapshot.WorkStepId,
            snapshot.WorkPlanVersion);
    }

    private static string[] ResolveHostPaths(AgentInvocationRequest request)
    {
        var probe = new SkillsInvocationContext(
            request.Snapshot.SkillCatalogVersion ?? string.Empty,
            request.Snapshot.SkillNames ?? [],
            request.Snapshot.SkillContentHashes ?? [],
            request.Snapshot.SkillSourceIds ?? [],
            request.Snapshot.SkillMaterializedPaths,
            IsHostMode: true,
            ActivationStore: request.SkillActivationStore,
            AllowlistSkillIds: request.SkillAllowlistIds,
            CatalogMaterializedPathsByName: request.SkillCatalogPathsByName,
            CatalogDescriptionsByName: request.SkillCatalogDescriptionsByName,
            ScopeSourceRoots: request.SkillScopeSourceRoots,
            SkillDiscoveryMode: request.SkillDiscoveryMode);
        return MafSkillsAgentFactory.ResolveHostLoadableMaterializedPaths(probe);
    }
}
