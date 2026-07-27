using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public static class RunRequestFactory
{
    public static NewSessionRunRequest CreateNewSessionRequest(RunRequestDraft draft)
    {
        Validate(draft);
        return new NewSessionRunRequest(
            $"mconsole-session-{Guid.NewGuid():N}",
            $"mconsole-run-{Guid.NewGuid():N}",
            draft.Mode,
            CreateSelection(draft),
            [new TextContentBlock(draft.Input)],
            draft.WorkspaceId);
    }

    public static ExistingSessionRunRequest CreateExistingSessionRequest(
        string sessionId,
        RunRequestDraft draft,
        int? expectedSelectionVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Validate(draft);
        return new ExistingSessionRunRequest(
            sessionId,
            $"mconsole-run-{Guid.NewGuid():N}",
            CreateSelection(draft),
            [new TextContentBlock(draft.Input)],
            expectedSelectionVersion);
    }

    public static NextTurnSelection CreateSelection(RunRequestDraft draft)
    {
        Validate(draft);
        var primary = CreateAgent(draft.PrimaryAgent);
        var additional = draft.AdditionalAgents?.Select(CreateAgent).ToArray() ?? [];
        ModeOptions modeOptions = draft.Mode switch
        {
            RuntimeMode.Expert => new ExpertModeOptions(primary),
            RuntimeMode.Meeting => CreateMeetingOptions(primary, additional),
            RuntimeMode.Work => new WorkModeOptions(
                primary,
                additional,
                WorkflowPolicy.Default),
            _ => throw new ArgumentOutOfRangeException(nameof(draft), draft.Mode, null),
        };

        return new NextTurnSelection(
            draft.SelectionVersion,
            draft.Mode,
            new DefaultSelection(draft.PrimaryAgent.ProviderId, draft.PrimaryAgent.ModelId),
            modeOptions,
            draft.SessionInstructions,
            draft.ToolCatalogVersion);
    }

    private static MeetingModeOptions CreateMeetingOptions(
        AgentRef primary,
        IReadOnlyList<AgentRef> additional)
    {
        var agents = new[] { primary }.Concat(additional).ToArray();
        return new MeetingModeOptions(
            agents.Select((agent, index) => new MeetingParticipant(
                    agent.AgentId,
                    agent,
                    agent.AgentId,
                    index))
                .ToArray());
    }

    private static AgentRef CreateAgent(RunAgentDraft draft) => new(
        draft.AgentId,
        draft.PromptTemplateVersion,
        draft.SystemPrompt,
        draft.ProviderId,
        draft.ModelId);

    private static void Validate(RunRequestDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Input);
        ValidateAgent(draft.PrimaryAgent);
        foreach (var agent in draft.AdditionalAgents ?? [])
        {
            ValidateAgent(agent);
        }

        if (draft.SelectionVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draft),
                "SelectionVersion must be positive.");
        }

        if (draft.Mode == RuntimeMode.Work
            && (draft.AdditionalAgents is null || draft.AdditionalAgents.Count == 0))
        {
            throw new ArgumentException(
                "Work mode requires at least one additional worker agent.",
                nameof(draft));
        }
    }

    private static void ValidateAgent(RunAgentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.PromptTemplateVersion);
    }
}
