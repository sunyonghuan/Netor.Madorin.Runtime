using System.Text.Json;

using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Menu;

namespace Madorin.AI.Runtime.MConsole.Testing;

public sealed class TestCaseRunner(
    string executablePath,
    CommandCatalog catalog,
    CommandDefaults defaults,
    ProcessRunner processRunner,
    FixtureLibrary fixtures,
    IHostTestActionExecutor? hostActions = null)
{
    public async Task<TestCaseReport> RunAsync(
        MConsoleTestCase testCase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var startedAt = DateTimeOffset.UtcNow;
        var context = new CaseExecutionContext();
        if (!string.IsNullOrWhiteSpace(defaults.Workspace))
        {
            context.Set("workspace", defaults.Workspace);
        }

        if (!string.IsNullOrWhiteSpace(defaults.DataDirectory))
        {
            context.Set("dataDir", defaults.DataDirectory);
        }

        var reports = new List<TestStepReport>();
        string? cleanupError = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(testCase.Fixtures.Before))
            {
                await fixtures.ExecuteAsync(testCase.Fixtures.Before, context, cancellationToken);
            }

            foreach (var step in testCase.Steps)
            {
                if (testCase.StopOnFailure && reports.Any(static item => !item.Passed))
                {
                    reports.Add(new TestStepReport(
                        step.Id,
                        Passed: false,
                        ExitCode: null,
                        Assertions: [],
                        Error: "Skipped after a previous failure.",
                        Data: null));
                    continue;
                }

                reports.Add(await ExecuteStepAsync(
                    testCase,
                    step,
                    context,
                    cancellationToken));
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(testCase.Fixtures.After))
            {
                try
                {
                    await fixtures.ExecuteAsync(testCase.Fixtures.After, context, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or UnauthorizedAccessException)
                {
                    cleanupError = ex.Message;
                }
            }
        }

        return new TestCaseReport(
            testCase.Name,
            startedAt,
            DateTimeOffset.UtcNow,
            reports.Count(static item => item.Passed),
            reports.Count(static item => !item.Passed && item.Error != "Skipped after a previous failure."),
            reports.Count(static item => item.Error == "Skipped after a previous failure."),
            reports.ToArray(),
            cleanupError);
    }

    private async Task<TestStepReport> ExecuteStepAsync(
        MConsoleTestCase testCase,
        TestStepDefinition step,
        CaseExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var arguments = context.Resolve(step.Arguments);
            return step.Kind switch
            {
                TestStepKind.Command => await ExecuteCommandStepAsync(
                    testCase,
                    step,
                    arguments,
                    context,
                    cancellationToken),
                TestStepKind.Host => await ExecuteHostStepAsync(
                    step,
                    arguments,
                    context,
                    cancellationToken),
                _ => throw new InvalidDataException($"Unknown step kind '{step.Kind}'."),
            };
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or IOException
            or TimeoutException)
        {
            return new TestStepReport(step.Id, false, null, [], ex.Message, null);
        }
    }

    private async Task<TestStepReport> ExecuteCommandStepAsync(
        MConsoleTestCase testCase,
        TestStepDefinition step,
        IReadOnlyDictionary<string, string?> arguments,
        CaseExecutionContext context,
        CancellationToken cancellationToken)
    {
        var command = catalog.Get(step.CommandId!);
        var stepDefaults = defaults with
        {
            Workspace = context.Values.TryGetValue("workspace", out var workspace)
                ? workspace
                : defaults.Workspace,
            DataDirectory = context.Values.TryGetValue("dataDir", out var dataDirectory)
                ? dataDirectory
                : defaults.DataDirectory,
        };
        var invocation = ParameterWizard.Build(command, stepDefaults, arguments);
        FileTreeSnapshot? before = null;
        FileTreeSnapshot? after = null;
        if (step.Assertions.Any(static item => item.Kind is AssertionKind.NoDataChange))
        {
            before = await FileTreeSnapshotService.CaptureAsync(
                stepDefaults.DataDirectory ?? stepDefaults.Workspace!,
                cancellationToken);
        }

        var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var outcome = await new CommandExecutionService(
                processRunner,
                new StringReader(string.Empty),
                output)
            .ExecuteAsync(
                executablePath,
                invocation,
                stepDefaults,
                TimeSpan.FromMinutes(2),
                testCase.AllowDangerousOperations,
                cancellationToken);
        if (outcome.Result is not { } execution)
        {
            return new TestStepReport(
                step.Id,
                false,
                null,
                [],
                string.Join(" | ", outcome.Validation.Errors),
                null);
        }

        if (before is not null)
        {
            after = await FileTreeSnapshotService.CaptureAsync(before.RootPath, cancellationToken);
        }

        var assertions = AssertionEngine.Evaluate(execution, step.Assertions, before, after).ToArray();
        JsonElement? data = execution.Envelope?.Data?.Clone();
        if (data is { } structured)
        {
            context.CaptureStepData(step.Id, structured);
        }

        return new TestStepReport(
            step.Id,
            assertions.All(static item => item.Passed),
            execution.ExitCode,
            assertions,
            null,
            data);
    }

    private async Task<TestStepReport> ExecuteHostStepAsync(
        TestStepDefinition step,
        IReadOnlyDictionary<string, string?> arguments,
        CaseExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (hostActions is null)
        {
            throw new InvalidOperationException("No host action executor is configured.");
        }

        var result = await hostActions.ExecuteAsync(
            step.HostAction!,
            arguments,
            cancellationToken);
        if (result.Data is { } data)
        {
            context.CaptureStepData(step.Id, data);
        }

        var assertions = result.Success
            ? Array.Empty<AssertionResult>()
            : [new AssertionResult(
                AssertionKind.HostRunTerminalState,
                false,
                "success",
                "failure",
                result.Error ?? "Host action failed.")];
        return new TestStepReport(
            step.Id,
            result.Success,
            null,
            assertions,
            result.Error,
            result.Data);
    }
}
