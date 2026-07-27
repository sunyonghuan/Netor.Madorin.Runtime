using Madorin.AI.Runtime.MConsole.Configuration;
using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed class P0SmokeRunner(MConsoleOptions options, string repositoryRoot)
{
    private readonly ProcessRunner _processRunner = new();

    public async Task<P0SmokeReport> RunAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.DefaultWorkspace))
        {
            throw new InvalidOperationException("P0 smoke requires --workspace or defaultWorkspace.");
        }

        var workspace = MConsolePaths.NormalizePath(options.DefaultWorkspace);
        var executable = await new MadorinLocator(_processRunner).LocateAsync(
            options.RuntimeExecutablePath,
            repositoryRoot,
            TimeSpan.FromSeconds(options.CommandTimeoutSeconds),
            cancellationToken);

        var doctorArguments = new List<string>
        {
            "doctor",
            "--json",
            "--no-color",
            "--workspace",
            workspace,
        };
        if (!string.IsNullOrWhiteSpace(options.DefaultDataDirectory))
        {
            doctorArguments.Add("--data-dir");
            doctorArguments.Add(MConsolePaths.NormalizePath(options.DefaultDataDirectory));
        }

        var doctor = await _processRunner.RunAsync(
            executable.Path,
            doctorArguments,
            timeout: TimeSpan.FromSeconds(options.CommandTimeoutSeconds),
            cancellationToken: cancellationToken);

        await using var host = new ServeHost(new ServeHostOptions(
            executable.Path,
            workspace,
            DataDirectory: string.IsNullOrWhiteSpace(options.DefaultDataDirectory)
                ? null
                : MConsolePaths.NormalizePath(options.DefaultDataDirectory),
            StartupTimeout: TimeSpan.FromSeconds(options.StartupTimeoutSeconds)));
        var started = await host.StartAsync(cancellationToken: cancellationToken);
        var runtimeSecret = host.RuntimeSecret;
        var stopped = await host.StopAsync(cancellationToken);
        var combinedOutput = string.Join(
            '\n',
            started.StandardOutputTail
                .Concat(started.StandardErrorTail)
                .Concat(stopped.StandardOutputTail)
                .Concat(stopped.StandardErrorTail));
        var secretLeakDetected = !string.IsNullOrEmpty(runtimeSecret)
            && combinedOutput.Contains(runtimeSecret, StringComparison.Ordinal);

        return new P0SmokeReport(
            executable,
            doctor,
            started,
            stopped,
            secretLeakDetected);
    }
}
