using System.Diagnostics;

using Madorin.AI.Runtime.MConsole.Security;

namespace Madorin.AI.Runtime.MConsole.Hosting;

public sealed class ProcessRunner(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ExecutionResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var startedAt = _timeProvider.GetUtcNow();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                executablePath,
                arguments,
                workingDirectory,
                standardInput is not null,
                environment),
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{executablePath}'.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CreateStartFailure(executablePath, arguments, startedAt, ex);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        using var timeoutSource = timeout is { } timeoutValue
            ? new CancellationTokenSource(timeoutValue)
            : new CancellationTokenSource();
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        var timedOut = false;
        var canceled = false;
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            canceled = cancellationToken.IsCancellationRequested;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        var secrets = environment?.Values ?? [];
        standardOutput = SecretRedactor.RedactText(standardOutput, secrets);
        standardError = SecretRedactor.RedactText(standardError, secrets);

        JsonEnvelope? envelope = null;
        _ = JsonEnvelope.TryParse(standardOutput, out envelope, out _);

        var exitCode = canceled ? 130 : timedOut ? 124 : process.ExitCode;
        return new ExecutionResult(
            executablePath,
            SecretRedactor.RedactArguments(arguments),
            exitCode,
            standardOutput,
            standardError,
            envelope,
            startedAt,
            _timeProvider.GetUtcNow() - startedAt,
            timedOut,
            canceled);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool redirectInput,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }

    private ExecutionResult CreateStartFailure(
        string command,
        IReadOnlyList<string> arguments,
        DateTimeOffset startedAt,
        Exception exception) => new(
            command,
            SecretRedactor.RedactArguments(arguments),
            -1,
            string.Empty,
            exception.Message,
            null,
            startedAt,
            _timeProvider.GetUtcNow() - startedAt,
            false,
            false);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
