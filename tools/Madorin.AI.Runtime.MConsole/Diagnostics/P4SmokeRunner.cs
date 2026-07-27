using Madorin.AI.Runtime.MConsole.Configuration;
using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Menu;

namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed class P4SmokeRunner(MConsoleOptions options, string repositoryRoot)
{
    public async Task<P4SmokeReport> RunAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.DefaultWorkspace))
        {
            throw new InvalidOperationException("P4 smoke requires --workspace or defaultWorkspace.");
        }

        var runner = new ProcessRunner();
        var executable = await new MadorinLocator(runner).LocateAsync(
            options.RuntimeExecutablePath,
            repositoryRoot,
            TimeSpan.FromSeconds(options.CommandTimeoutSeconds),
            cancellationToken);
        var defaults = CommandDefaults.FromOptions(options);
        var catalog = CommandCatalog.CreateDefault();
        var commands = new List<P4CommandSmokeResult>();
        foreach (var invocation in CreateInvocations(catalog, defaults))
        {
            var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var outcome = await new CommandExecutionService(
                    runner,
                    new StringReader(string.Empty),
                    output)
                .ExecuteAsync(
                    executable.Path,
                    invocation,
                    defaults,
                    TimeSpan.FromSeconds(options.CommandTimeoutSeconds),
                    cancellationToken: cancellationToken);
            commands.Add(new P4CommandSmokeResult(
                invocation.Definition.Id,
                outcome.Result?.ExitCode ?? -1));
        }

        return new P4SmokeReport(
            catalog.Groups.Count,
            catalog.Commands.Count,
            commands,
            RunConflictBlocked: IsBlocked(
                catalog,
                defaults,
                "run",
                Values(("--input", "a"), ("--input-file", "b"))),
            SessionDeleteBlocked: IsBlocked(
                catalog,
                defaults,
                "session.delete",
                Values(("session-id", "missing"))),
            StorageConflictBlocked: IsBlocked(
                catalog,
                defaults,
                "storage.gc",
                Values(("--dry-run", "true"), ("--confirm", "true"))));
    }

    private static IEnumerable<CommandInvocation> CreateInvocations(
        CommandCatalog catalog,
        CommandDefaults defaults)
    {
        yield return Build(catalog, defaults, "version", Values());
        yield return Build(catalog, defaults, "doctor", Values());
        yield return Build(catalog, defaults, "config.show", Values());
        yield return Build(catalog, defaults, "agent.list", Values());
        yield return Build(catalog, defaults, "memory.show", Values(("scope", "global")));
        yield return Build(catalog, defaults, "session.list", Values());
        yield return Build(catalog, defaults, "skill.list", Values());
        yield return Build(catalog, defaults, "db.check", Values());
        yield return Build(catalog, defaults, "storage.check", Values());
    }

    private static CommandInvocation Build(
        CommandCatalog catalog,
        CommandDefaults defaults,
        string id,
        IReadOnlyDictionary<string, string?> values) =>
        ParameterWizard.Build(catalog.Get(id), defaults, values);

    private static bool IsBlocked(
        CommandCatalog catalog,
        CommandDefaults defaults,
        string id,
        IReadOnlyDictionary<string, string?> values) =>
        !CommandInvocationValidator.Validate(Build(catalog, defaults, id, values), defaults).IsValid;

    private static Dictionary<string, string?> Values(
        params (string Name, string? Value)[] values) =>
        values.ToDictionary(static item => item.Name, static item => item.Value, StringComparer.Ordinal);
}
