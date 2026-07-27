using System.Diagnostics;
using System.Security.Cryptography;

using Madorin.AI.Runtime.MConsole.Security;

namespace Madorin.AI.Runtime.MConsole.Hosting;

public sealed class ServeHost : IAsyncDisposable
{
    private readonly ServeHostOptions _options;
    private readonly BoundedLineBuffer _standardOutput;
    private readonly BoundedLineBuffer _standardError;
    private Process? _process;
    private string? _secret;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _exitedAt;

    public ServeHost(ServeHostOptions options)
    {
        options.Validate();
        _options = options;
        _standardOutput = new BoundedLineBuffer(options.RetainedLogLines);
        _standardError = new BoundedLineBuffer(options.RetainedLogLines);
    }

    public string? RuntimeSecret => _process is { HasExited: false } ? _secret : null;

    public async Task<ServeHostSnapshot> StartAsync(
        Func<CancellationToken, Task<bool>>? readinessProbe = null,
        CancellationToken cancellationToken = default)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("ServeHost can only be started once.");
        }

        _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var startInfo = CreateStartInfo();
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
            _standardOutput.Add(SecretRedactor.RedactText(eventArgs.Data ?? string.Empty, [_secret]));
        process.ErrorDataReceived += (_, eventArgs) =>
            _standardError.Add(SecretRedactor.RedactText(eventArgs.Data ?? string.Empty, [_secret]));
        process.Exited += (_, _) => _exitedAt = DateTimeOffset.UtcNow;

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the Runtime serve process.");
        }

        _process = process;
        _startedAt = DateTimeOffset.UtcNow;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.StandardInput.WriteLineAsync(_secret.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        using var timeoutSource = new CancellationTokenSource(_options.ResolvedStartupTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await WaitUntilReadyAsync(readinessProbe, linkedSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None);
            throw new TimeoutException(
                $"Runtime serve process did not become ready within {_options.ResolvedStartupTimeout}.");
        }
        catch
        {
            await StopAsync(CancellationToken.None);
            throw;
        }

        return GetSnapshot();
    }

    public ServeHostSnapshot GetSnapshot()
    {
        var process = _process ?? throw new InvalidOperationException("ServeHost has not been started.");
        var hasExited = process.HasExited;
        return new ServeHostSnapshot(
            process.Id,
            !hasExited,
            hasExited ? process.ExitCode : null,
            _startedAt,
            hasExited ? _exitedAt ?? DateTimeOffset.UtcNow : null,
            _standardOutput.Snapshot(),
            _standardError.Snapshot());
    }

    public async Task<ServeHostSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process ?? throw new InvalidOperationException("ServeHost has not been started.");
        if (!process.HasExited)
        {
            _ = process.CloseMainWindow();
            using var timeoutSource = new CancellationTokenSource(_options.ResolvedShutdownTimeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(linkedSource.Token);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        _exitedAt ??= DateTimeOffset.UtcNow;
        _secret = null;
        return GetSnapshot();
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                await StopAsync(CancellationToken.None);
            }

            _process.Dispose();
            _process = null;
        }

        _secret = null;
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            WorkingDirectory = _options.WorkspaceDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("serve");
        AddOption(startInfo, "--workspace", _options.WorkspaceDirectory);
        AddOption(startInfo, "--instance", _options.InstanceId);
        AddOption(startInfo, "--pipe-prefix", _options.PipePrefix);
        AddOption(startInfo, "--data-dir", _options.DataDirectory);
        AddOption(startInfo, "--log-dir", _options.LogDirectory);
        if (_options.MaxRuns is { } maxRuns)
        {
            AddOption(startInfo, "--max-runs", maxRuns.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return startInfo;
    }

    private async Task WaitUntilReadyAsync(
        Func<CancellationToken, Task<bool>>? readinessProbe,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process?.HasExited is true)
            {
                throw new InvalidOperationException(
                    $"Runtime serve process exited before becoming ready (exit={_process.ExitCode}).");
            }

            if (readinessProbe is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                if (_process?.HasExited is false)
                {
                    return;
                }
            }
            else if (await readinessProbe(cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static void AddOption(ProcessStartInfo startInfo, string option, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        startInfo.ArgumentList.Add(option);
        startInfo.ArgumentList.Add(value);
    }
}
