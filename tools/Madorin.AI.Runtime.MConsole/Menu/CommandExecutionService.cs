using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Security;

namespace Madorin.AI.Runtime.MConsole.Menu;

public sealed record CommandExecutionOutcome(
    CommandValidationResult Validation,
    bool ConfirmationAccepted,
    ExecutionResult? Result);

public sealed class CommandExecutionService(
    ProcessRunner processRunner,
    TextReader input,
    TextWriter output)
{
    private const string RuntimeSecretEnvironmentVariable = "MADORIN_AI_RUNTIME_SECRET";

    public async Task<CommandExecutionOutcome> ExecuteAsync(
        string executablePath,
        CommandInvocation invocation,
        CommandDefaults defaults,
        TimeSpan timeout,
        bool allowDangerousWithoutPrompt = false,
        CancellationToken cancellationToken = default)
    {
        var validation = CommandInvocationValidator.Validate(invocation, defaults);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                await output.WriteLineAsync("Validation: " + error);
            }

            return new CommandExecutionOutcome(validation, false, null);
        }

        await output.WriteLineAsync(
            "Command: " + string.Join(' ', SecretRedactor.RedactArguments(invocation.Arguments)));
        var confirmed = await DangerousCommandGuard.ConfirmAsync(
            invocation,
            defaults,
            input,
            output,
            allowDangerousWithoutPrompt,
            cancellationToken);
        if (!confirmed)
        {
            await output.WriteLineAsync("Command cancelled before process start.");
            return new CommandExecutionOutcome(validation, false, null);
        }

        var environment = new Dictionary<string, string?>(
            invocation.Environment,
            StringComparer.Ordinal);
        if (invocation.Definition.UsesRuntimeSecret)
        {
            var runtimeSecret = Environment.GetEnvironmentVariable(
                RuntimeSecretEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(runtimeSecret))
            {
                var error = new CommandValidationResult(
                    false,
                    [$"{RuntimeSecretEnvironmentVariable} is required for online control."]);
                await output.WriteLineAsync("Validation: " + error.Errors[0]);
                return new CommandExecutionOutcome(error, true, null);
            }

            environment[RuntimeSecretEnvironmentVariable] = runtimeSecret;
        }

        foreach (var parameter in invocation.Definition.Parameters
            .Where(static item => item.Kind is ParameterKind.SecretEnvName))
        {
            if (!invocation.Values.TryGetValue(parameter.Name, out var variableName)
                || string.IsNullOrWhiteSpace(variableName))
            {
                continue;
            }

            environment[variableName] = Environment.GetEnvironmentVariable(variableName);
        }

        var result = await processRunner.RunAsync(
            executablePath,
            invocation.Arguments,
            workingDirectory: defaults.Workspace,
            standardInput: invocation.StandardInput,
            environment: environment,
            timeout: timeout,
            cancellationToken: cancellationToken);
        return new CommandExecutionOutcome(validation, true, result);
    }
}
