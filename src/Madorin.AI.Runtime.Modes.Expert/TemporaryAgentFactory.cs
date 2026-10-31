using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Modes.Expert;

/// <summary>Creates short-lived, least-privilege agents for one delegated task.</summary>
internal static class TemporaryAgentFactory
{
    private static readonly string[] DefaultReadOnlyTools =
    [
        "mcli_fs_read",
        "mcli_fs_attach",
        "mcli_fs_search",
        "mcli_fs_inspect",
        "mcli_fs_list"
    ];

    internal static IReadOnlyList<string> DefaultReadOnlyToolIds => DefaultReadOnlyTools;

    private static readonly HashSet<string> DangerousTools = new(StringComparer.Ordinal)
    {
        "mcli_process",
        "mcli_http",
        "mcli_fs_copy",
        "mcli_fs_move",
        "mcli_memory_write",
        "mcli_memory_append",
        "mcli_memory_update",
        "mcli_memory_delete",
        "mcli_agent_job_status",
        "mcli_agent_job_result",
        "mcli_agent_job_wait",
        "mcli_agent_job_cancel",
        "mcli_expert_get_plan",
        "mcli_delegate_agent",
        "mcli_agent_job_create",
        "mcli_agent_job_create_many",
        "mcli_expert_create_plan",
        "mcli_expert_execute_step",
        "mcli_expert_update_step_status",
        "mcli_expert_abandon_plan"
    };

    public static AgentRef Create(
        AgentRef parent,
        string role,
        string task,
        string[]? requestedCapabilities,
        string[]? requestedToolIds)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);

        var requestedTools = requestedToolIds ?? DefaultReadOnlyTools;
        var parentTools = parent.AllowedToolIds is not null
            ? parent.AllowedToolIds.ToHashSet(StringComparer.Ordinal)
            : requestedTools.ToHashSet(StringComparer.Ordinal);
        var allowedTools = requestedTools
            .Where(parentTools.Contains)
            .Where(static tool => !DangerousTools.Contains(tool))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var capabilities = requestedCapabilities is { Length: > 0 }
            ? requestedCapabilities.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : parent.Capabilities;
        var prompt = $"You are a temporary {role}. Complete only this one task and return a concise factual result. " +
            "Only use the explicitly allowed tools. Do not run processes, access the network, delegate to another agent, or create plans. File writes and deletes are allowed only when the parent grant and requested tool list allow them.\n\n" +
            $"Task:\n{task}";

        return new AgentRef(
            $"temp-{Guid.NewGuid():N}",
            "temporary-1",
            prompt,
            parent.ProviderId,
            parent.ModelId,
            allowedTools,
            SkillIds: null,
            SkillDiscoveryMode: "direct",
            DisplayName: role,
            Description: "Ephemeral delegated agent",
            Capabilities: capabilities);
    }
}
