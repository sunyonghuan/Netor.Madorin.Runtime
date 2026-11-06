using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Services.Tools;

namespace Madorin.AI.Runtime.Cli.Tools;

internal enum StandaloneToolPermissionMode
{
    AskAll,
    AutoTools,
    AutoAll
}

internal static class StandaloneToolConsentPolicy
{
    public static bool TryParsePermissionMode(
        string? value,
        out StandaloneToolPermissionMode mode)
    {
        mode = StandaloneToolPermissionMode.AskAll;
        if (value is null)
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "ask-all":
                mode = StandaloneToolPermissionMode.AskAll;
                return true;
            case "auto-tools":
                mode = StandaloneToolPermissionMode.AutoTools;
                return true;
            case "auto-all":
                mode = StandaloneToolPermissionMode.AutoAll;
                return true;
            default:
                return false;
        }
    }

    public static string GetPermissionModeName(StandaloneToolPermissionMode mode) =>
        mode switch
        {
            StandaloneToolPermissionMode.AskAll => "ask-all",
            StandaloneToolPermissionMode.AutoTools => "auto-tools",
            StandaloneToolPermissionMode.AutoAll => "auto-all",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    public static bool IsInteractiveConsentEnabled(
        ICliTerminal terminal,
        bool isTextOutput,
        bool hasOutputFile)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return terminal.IsInteractive && isTextOutput && !hasOutputFile;
    }

    public static bool CanGrantForRun(ToolPermissionConsentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ScopeResolution is { IsSuccess: true, Scope.CanGrantForRun: true };
    }
}
