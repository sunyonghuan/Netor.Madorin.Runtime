using Madorin.AI.Runtime.MConsole.Menu;

namespace Madorin.AI.Runtime.MConsole.Testing;

public static class DefaultAssertionFactory
{
    public static IReadOnlyList<AssertionDefinition> Create(
        CommandDefinition command,
        bool jsonEnabled,
        bool dryRun = false,
        bool includeNoDataChange = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        var assertions = new List<AssertionDefinition>();
        if (command.Id.StartsWith("ctl.", StringComparison.Ordinal))
        {
            assertions.Add(new AssertionDefinition(
                AssertionKind.ExitCodeIn,
                ExpectedAny: ["0", "3", "5"],
                Source: "CommandCatalog: online control stable exit codes"));
        }
        else
        {
            assertions.Add(new AssertionDefinition(
                AssertionKind.ExitCodeEquals,
                Expected: "0",
                Source: "CommandCatalog: normal success"));
        }

        if (jsonEnabled && !command.IsRepl)
        {
            assertions.Add(new AssertionDefinition(
                AssertionKind.JsonSuccess,
                Expected: "true",
                Source: "Global JSON default"));
        }

        if (includeNoDataChange
            || command.ReadWriteKind is CommandReadWriteKind.ReadOnly
            || dryRun && command.PreferDryRun)
        {
            assertions.Add(new AssertionDefinition(
                AssertionKind.NoDataChange,
                Source: "CommandCatalog: read-only/dry-run"));
        }

        assertions.Add(new AssertionDefinition(
            AssertionKind.NoAnsi,
            Source: "Global no-color default"));
        return assertions;
    }
}
