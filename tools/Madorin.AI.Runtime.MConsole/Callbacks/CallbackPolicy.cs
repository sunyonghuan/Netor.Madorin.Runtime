namespace Madorin.AI.Runtime.MConsole.Callbacks;

public enum CallbackPolicyMode
{
    Manual,
    DenyAll,
    AllowListedTools,
}

public sealed record CallbackPolicy(
    CallbackPolicyMode Mode,
    IReadOnlySet<string>? AllowedToolIds = null,
    bool AllowLowRiskApprovals = false)
{
    public static CallbackPolicy Manual { get; } = new(CallbackPolicyMode.Manual);

    public static CallbackPolicy DenyAll { get; } = new(CallbackPolicyMode.DenyAll);

    public bool AllowsTool(string toolId) =>
        Mode is CallbackPolicyMode.AllowListedTools
        && AllowedToolIds?.Contains(toolId) is true;
}
