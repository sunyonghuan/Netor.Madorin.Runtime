namespace Madorin.AI.Runtime.MConsole.Callbacks;

public enum HostCallbackKind
{
    ToolPermission,
    Approval,
    ToolCall,
    ToolResultQuery,
    ToolCallCancel,
}

public sealed record CallbackRequestEnvelope(
    string CallbackId,
    HostCallbackKind Kind,
    string RuntimeInstanceId,
    string? RunId,
    string? CallId,
    string? AgentId,
    string? GrantId,
    string? ToolId,
    string Summary,
    DateTimeOffset ArrivedAt,
    DateTimeOffset Deadline,
    CallbackPolicyMode PolicyMode);
