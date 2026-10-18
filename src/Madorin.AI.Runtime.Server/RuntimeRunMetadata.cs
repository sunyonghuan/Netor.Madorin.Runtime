using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Server;

internal static class RuntimeRunMetadata
{
    public static string ComputeRequestHash(NewSessionRunRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            RuntimeJsonContext.Default.NewSessionRunRequest);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static string ComputeRequestHash(ExistingSessionRunRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            RuntimeJsonContext.Default.ExistingSessionRunRequest);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static NextTurnSelection RemovePromptContent(NextTurnSelection selection)
    {
        ModeOptions modeOptions = selection.ModeOptions switch
        {
            // Expert 须保留 AvailableAgents，否则落盘/续跑后 @ 委派池丢失。
            ExpertModeOptions expert => expert with
            {
                Agent = RemovePromptContent(expert.Agent),
                AvailableAgents = expert.AvailableAgents is null
                    ? null
                    : [.. expert.AvailableAgents.Select(RemovePromptContent)]
            },
            _ => throw new InvalidOperationException(
                $"Mode options '{selection.ModeOptions.GetType().Name}' are not supported.")
        };
        return selection with { ModeOptions = modeOptions };
    }

    public static AgentSnapshot[] CreateAgentSnapshots(NextTurnSelection selection)
    {
        IEnumerable<AgentRef> agents = selection.ModeOptions switch
        {
            ExpertModeOptions expert => [expert.Agent, .. expert.AvailableAgents ?? []],
            _ => throw new InvalidOperationException(
                $"Mode options '{selection.ModeOptions.GetType().Name}' are not supported.")
        };

        return
        [
            .. agents
                .DistinctBy(static a => a.AgentId, StringComparer.Ordinal)
                .OrderBy(static a => a.AgentId, StringComparer.Ordinal)
                .Select(static agent => new AgentSnapshot(
                    agent.AgentId,
                    agent.PromptTemplateVersion,
                    ComputePromptHash(agent.SystemPrompt),
                    agent.ProviderId,
                    agent.ModelId))
        ];
    }

    public static string ComputePromptHash(string prompt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));

    private static AgentRef RemovePromptContent(AgentRef agent) =>
        agent with { SystemPrompt = string.Empty };
}
