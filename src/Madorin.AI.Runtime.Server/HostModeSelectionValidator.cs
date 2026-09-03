using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Server;

/// <summary>
/// Host-mode Selection / Agent / ProviderId validation (doc 22 §5.5).
/// Multi-vendor non-primary agents are allowed when their ProviderId is resolvable (P2).
/// </summary>
internal static class HostModeSelectionValidator
{
    public static string? ValidateForRun(
        NextTurnSelection selection,
        RuntimeMode sessionMode,
        HostProviderRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.Mode != sessionMode)
        {
            return "The Run mode and Selection mode must match.";
        }

        var defaultProviderId = NormalizeRequired(
            selection.DefaultSelection.ProviderId,
            "DefaultSelection.ProviderId");
        if (defaultProviderId is null)
        {
            return "DefaultSelection.ProviderId is required.";
        }

        var defaultModelId = NormalizeRequired(
            selection.DefaultSelection.ModelId,
            "DefaultSelection.ModelId");
        if (defaultModelId is null)
        {
            return "DefaultSelection.ModelId is required.";
        }

        switch (selection.ModeOptions)
        {
            case ExpertModeOptions expert:
            {
                var agentError = ValidateCompleteAgent(expert.Agent, "Expert Agent");
                if (agentError is not null)
                {
                    return agentError;
                }

                var primaryError = ValidatePrimaryAlignment(
                    expert.Agent,
                    defaultProviderId,
                    defaultModelId,
                    "Expert Agent");
                if (primaryError is not null)
                {
                    return primaryError;
                }

                break;
            }

            case WorkModeOptions work:
            {
                var gmError = ValidateCompleteAgent(work.GeneralManager, "Work GeneralManager");
                if (gmError is not null)
                {
                    return gmError;
                }

                if (work.AvailableAgents is null || work.AvailableAgents.Length == 0)
                {
                    return "Work mode requires at least one available child Agent.";
                }

                for (var i = 0; i < work.AvailableAgents.Length; i++)
                {
                    var childError = ValidateCompleteAgent(
                        work.AvailableAgents[i],
                        $"Work AvailableAgents[{i}]");
                    if (childError is not null)
                    {
                        return childError;
                    }
                }

                var primaryError = ValidatePrimaryAlignment(
                    work.GeneralManager,
                    defaultProviderId,
                    defaultModelId,
                    "Work GeneralManager");
                if (primaryError is not null)
                {
                    return primaryError;
                }

                break;
            }

            case MeetingModeOptions meeting:
            {
                if (meeting.Participants is null || meeting.Participants.Length == 0)
                {
                    return "Meeting mode requires at least one participant.";
                }

                for (var i = 0; i < meeting.Participants.Length; i++)
                {
                    var participantError = ValidateCompleteAgent(
                        meeting.Participants[i].Agent,
                        $"Meeting Participants[{i}].Agent");
                    if (participantError is not null)
                    {
                        return participantError;
                    }
                }

                break;
            }

            default:
                return $"Mode options type '{selection.ModeOptions.GetType().Name}' is not supported.";
        }

        if (registry is null)
        {
            return null;
        }

        foreach (var providerId in CollectUsedProviderIds(selection))
        {
            if (!registry.Contains(providerId))
            {
                return $"Provider '{providerId}' is not configured for this Runtime instance.";
            }
        }

        return null;
    }

    public static IReadOnlyList<string> CollectUsedProviderIds(NextTurnSelection selection)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var defaultId = NormalizeOptional(selection.DefaultSelection.ProviderId);
        if (defaultId is not null)
        {
            ids.Add(defaultId);
        }

        foreach (var agent in EnumerateAgents(selection.ModeOptions))
        {
            var providerId = NormalizeOptional(agent.ProviderId);
            if (providerId is not null)
            {
                ids.Add(providerId);
            }
        }

        return [.. ids];
    }

    public static string ResolvePrimaryProviderId(NextTurnSelection selection)
    {
        // Host mode authority: DefaultSelection.ProviderId after alignment validation.
        return selection.DefaultSelection.ProviderId.Trim();
    }

    private static IEnumerable<AgentRef> EnumerateAgents(ModeOptions options)
    {
        switch (options)
        {
            case ExpertModeOptions expert:
                yield return expert.Agent;
                break;

            case WorkModeOptions work:
                yield return work.GeneralManager;
                if (work.AvailableAgents is not null)
                {
                    foreach (var agent in work.AvailableAgents)
                    {
                        yield return agent;
                    }
                }

                break;

            case MeetingModeOptions meeting:
                if (meeting.Participants is not null)
                {
                    foreach (var participant in meeting.Participants)
                    {
                        yield return participant.Agent;
                    }
                }

                if (meeting.HostAgent is not null)
                {
                    yield return meeting.HostAgent;
                }

                if (meeting.EffectiveSelectorPolicy.SelectorAgent is not null)
                {
                    yield return meeting.EffectiveSelectorPolicy.SelectorAgent;
                }

                if (meeting.EffectivePolicy.Summarizer is not null)
                {
                    yield return meeting.EffectivePolicy.Summarizer;
                }

                break;
        }
    }

    private static string? ValidateCompleteAgent(AgentRef agent, string label)
    {
        if (NormalizeRequired(agent.AgentId, null) is null)
        {
            return $"{label}.AgentId is required.";
        }

        if (NormalizeRequired(agent.PromptTemplateVersion, null) is null)
        {
            return $"{label}.PromptTemplateVersion is required.";
        }

        if (NormalizeRequired(agent.SystemPrompt, null) is null)
        {
            return $"{label}.SystemPrompt is required.";
        }

        return null;
    }

    private static string? ValidatePrimaryAlignment(
        AgentRef agent,
        string defaultProviderId,
        string defaultModelId,
        string label)
    {
        var providerError = CompareOptionalField(
            agent.ProviderId,
            defaultProviderId,
            $"{label}.ProviderId",
            "DefaultSelection.ProviderId");
        if (providerError is not null)
        {
            return providerError;
        }

        var modelError = CompareOptionalField(
            agent.ModelId,
            defaultModelId,
            $"{label}.ModelId",
            "DefaultSelection.ModelId");
        if (modelError is not null)
        {
            return modelError;
        }

        return null;
    }

    private static string? CompareOptionalField(
        string? agentValue,
        string defaultValue,
        string agentLabel,
        string defaultLabel)
    {
        var normalizedAgent = NormalizeOptional(agentValue);
        if (normalizedAgent is null)
        {
            return null;
        }

        if (!string.Equals(normalizedAgent, defaultValue, StringComparison.Ordinal))
        {
            return $"{agentLabel} must equal {defaultLabel} when both are set.";
        }

        return null;
    }

    private static string? NormalizeRequired(string? value, string? _)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
