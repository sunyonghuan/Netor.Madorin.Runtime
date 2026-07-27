using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Menu;

public sealed class SkillHelpService(ProcessRunner processRunner)
{
    public async Task<string?> TryGetHelpAsync(
        string executablePath,
        CommandDefinition definition,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.Id.StartsWith("skill.", StringComparison.Ordinal))
        {
            return null;
        }

        var arguments = definition.CommandPath.Append("--help").ToArray();
        var result = await processRunner.RunAsync(
            executablePath,
            arguments,
            workingDirectory,
            timeout: timeout,
            cancellationToken: cancellationToken);
        return result.IsSuccess && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardOutput
            : null;
    }
}
