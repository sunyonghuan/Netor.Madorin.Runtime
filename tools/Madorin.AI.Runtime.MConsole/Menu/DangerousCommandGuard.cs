namespace Madorin.AI.Runtime.MConsole.Menu;

public static class DangerousCommandGuard
{
    public static async Task<bool> ConfirmAsync(
        CommandInvocation invocation,
        CommandDefaults defaults,
        TextReader input,
        TextWriter output,
        bool allowWithoutPrompt = false,
        CancellationToken cancellationToken = default)
    {
        if (!invocation.Definition.RequiresConfirmation)
        {
            return true;
        }

        await output.WriteLineAsync(
            $"Risk: {invocation.Definition.RiskLevel}; " +
            $"write={invocation.Definition.ReadWriteKind}; lock={invocation.Definition.LockKind}");
        await output.WriteLineAsync($"Workspace: {defaults.Workspace ?? "<not configured>"}");
        await output.WriteLineAsync($"Data directory: {defaults.DataDirectory ?? "<default>"}");
        if (invocation.Definition.PreferDryRun
            && !IsEnabled(invocation, "--dry-run"))
        {
            await output.WriteLineAsync("This command supports dry-run; run that first when possible.");
        }

        if (allowWithoutPrompt)
        {
            return true;
        }

        await output.WriteAsync($"Type '{invocation.Definition.Id}' to confirm: ");
        var confirmation = await input.ReadLineAsync(cancellationToken);
        return string.Equals(
            confirmation,
            invocation.Definition.Id,
            StringComparison.Ordinal);
    }

    private static bool IsEnabled(CommandInvocation invocation, string name) =>
        invocation.Values.TryGetValue(name, out var value)
        && bool.TryParse(value, out var enabled)
        && enabled;
}
