using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Skills;

/// <summary>Executes Runtime-owned load_skill / read_skill_resource / run_skill_script.</summary>
public sealed partial class SkillRuntimeToolExecutor : IToolExecutor
{
    private readonly string _workspaceRoot;
    private readonly string _runId;
    private readonly Func<SkillCatalogSnapshot?> _snapshotProvider;
    private readonly SkillActivationStore? _activationStore;
    private readonly bool _isHostMode;
    private readonly IReadOnlyDictionary<string, AgentRef> _agents;

    public SkillRuntimeToolExecutor(
        string workspaceRoot,
        string runId,
        Func<SkillCatalogSnapshot?> snapshotProvider,
        IReadOnlyDictionary<string, AgentRef> agents,
        SkillActivationStore? activationStore = null,
        bool isHostMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _runId = runId;
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _activationStore = activationStore;
        _isHostMode = isHostMode;
    }

    public bool CanExecute(string toolId) => SkillRuntimeTools.IsSkillRuntimeTool(toolId);

    public async ValueTask<IPreparedToolExecution> PrepareAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        var snapshot = ResolveSnapshot();
        return invocation.ToolId switch
        {
            SkillRuntimeTools.LoadToolId => PrepareLoad(invocation, snapshot),
            SkillRuntimeTools.ReadToolId => await PrepareUnifiedReadAsync(invocation, snapshot, ct)
                .ConfigureAwait(false),
            SkillRuntimeTools.ReadResourceToolId => await PrepareReadAsync(invocation, snapshot, ct)
                .ConfigureAwait(false),
            SkillRuntimeTools.RunToolId => PrepareRun(invocation, snapshot),
            _ => throw new InvalidOperationException($"Unsupported skill tool '{invocation.ToolId}'.")
        };
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        await using var prepared = await PrepareAsync(invocation, ct).ConfigureAwait(false);
        return await prepared.ExecuteAsync(ct).ConfigureAwait(false);
    }

    private SkillCatalogSnapshot ResolveSnapshot() =>
        _snapshotProvider()
        ?? throw new SkillScriptException(
            RuntimeErrorCodes.SkillRefreshFailed,
            "The Skill catalog was not captured by this Run.");

    private SkillCatalogEntry RequireSkill(
        ToolInvocation invocation,
        SkillCatalogSnapshot snapshot,
        string name)
    {
        _agents.TryGetValue(invocation.AgentId, out var agent);
        var allowlist = agent is null && _agents.Count > 0 ? [] : agent?.SkillIds;
        var filtered = snapshot.FilterBySkillIds(allowlist);
        if (!filtered.TryGetSkill(name, out var skill) || skill is null)
        {
            throw new SkillScriptException(
                RuntimeErrorCodes.SkillToolUnavailable,
                $"Skill '{name}' is not available to this Agent.");
        }

        if (_isHostMode
            && !SkillDiscoveryModes.IsDirect(agent?.SkillDiscoveryMode)
            && (_activationStore is null || !_activationStore.IsLoadable(name, allowlist)))
        {
            throw new SkillScriptException(
                RuntimeErrorCodes.SkillToolUnavailable,
                $"Skill '{name}' is not activated. Call skill.search first.");
        }

        return skill;
    }

    private static string ReadRequired(string argumentsJson, string name)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        if (!document.RootElement.TryGetProperty(name, out var value)
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new SkillScriptException(
                RuntimeErrorCodes.ToolArgumentsInvalid,
                $"Skill tool argument '{name}' is required.");
        }

        return value.GetString()!;
    }

    private sealed class Immediate(
        ToolInvocation invocation,
        string json,
        bool success = true,
        string? error = null) : IPreparedToolExecution
    {
        public string TargetSummary => invocation.ToolId;

        public Task<ToolResult> ExecuteAsync(CancellationToken ct = default) =>
            Task.FromResult(new ToolResult(invocation.CallId, invocation.ToolId, success, json, error));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
