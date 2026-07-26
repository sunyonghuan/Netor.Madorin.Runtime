using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Services.Tools;

namespace Madorin.AI.Runtime.Cli.Tools;

internal static class StandaloneToolConsentPolicy
{
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
