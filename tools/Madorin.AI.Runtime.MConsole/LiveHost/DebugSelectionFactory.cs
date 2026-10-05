using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.MConsole.LiveHost;

/// <summary>
/// 按当前 CLI 的模式规则组装下一轮 Selection。
/// 工作模式至少带一个执行代理，会议模式至少两个参与者。
/// </summary>
public static class DebugSelectionFactory
{
    public static NextTurnSelection Create(
        RuntimeMode mode,
        DebugConfigSnapshot config,
        string providerId,
        string modelId,
        int selectionVersion)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(selectionVersion);

        var primary = CreatePrimary(config, providerId, modelId);
        var others = config.Agents
            .Where(agent => !agent.Id.Equals(primary.AgentId, StringComparison.Ordinal))
            .Select(agent => CreateAgent(agent, providerId, modelId))
            .ToArray();
        ModeOptions options = mode switch
        {
            RuntimeMode.Expert => new ExpertModeOptions(primary),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
        return new NextTurnSelection(
            selectionVersion,
            mode,
            new DefaultSelection(providerId, modelId),
            options);
    }

    private static AgentRef CreatePrimary(DebugConfigSnapshot config, string providerId, string modelId)
    {
        DebugAgentInfo? source = null;
        for (var index = 0; index < config.Agents.Count; index++)
        {
            if (config.Agents[index].Id.Equals(config.DefaultAgentId, StringComparison.Ordinal))
            {
                source = config.Agents[index];
                break;
            }
        }

        source ??= config.Agents.Count == 0 ? null : config.Agents[0];
        if (source is null)
        {
            return new AgentRef("default", "1.0", DebugConfigSnapshot.DefaultPrompt, providerId, modelId);
        }

        return new AgentRef(
            source.Id,
            "1.0",
            PromptOf(source),
            providerId,
            modelId);
    }

    private static AgentRef CreateAgent(DebugAgentInfo agent, string providerId, string modelId) =>
        new(
            agent.Id,
            "1.0",
            PromptOf(agent),
            string.IsNullOrWhiteSpace(agent.Provider) ? providerId : agent.Provider,
            string.IsNullOrWhiteSpace(agent.Model) ? modelId : agent.Model);

    private static string PromptOf(DebugAgentInfo agent) =>
        string.IsNullOrWhiteSpace(agent.SystemPrompt)
            ? DebugConfigSnapshot.DefaultPrompt
            : agent.SystemPrompt;
}
