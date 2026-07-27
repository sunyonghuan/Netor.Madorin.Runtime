using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Madorin.AI.Runtime.MConsole.Hosting;

public sealed class ReplSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<ReplOutputLine> _output = Channel.CreateUnbounded<ReplOutputLine>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly TaskCompletionSource<int> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;

    public ReplSession(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, eventArgs) => Publish(eventArgs.Data, isError: false);
        _process.ErrorDataReceived += (_, eventArgs) => Publish(eventArgs.Data, isError: true);
        _process.Exited += (_, _) =>
        {
            _completion.TrySetResult(_process.ExitCode);
            _output.Writer.TryComplete();
        };
    }

    public Task<int> Completion => _completion.Task;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_started)
        {
            throw new InvalidOperationException("REPL session has already been started.");
        }

        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start the REPL process.");
        }

        _started = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return Task.CompletedTask;
    }

    public async Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public async Task CompleteInputAsync(CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        await _process.StandardInput.FlushAsync(cancellationToken);
        _process.StandardInput.Close();
    }

    public async IAsyncEnumerable<ReplOutputLine> ReadOutputAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var line in _output.Reader.ReadAllAsync(cancellationToken))
        {
            yield return line;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_started && !_process.HasExited)
        {
            try
            {
                await SendLineAsync("/exit", CancellationToken.None);
                using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }

        _output.Writer.TryComplete();
        _process.Dispose();
    }

    private void Publish(string? value, bool isError)
    {
        if (value is not null)
        {
            _output.Writer.TryWrite(new ReplOutputLine(DateTimeOffset.UtcNow, isError, value));
        }
    }

    private void EnsureRunning()
    {
        if (!_started)
        {
            throw new InvalidOperationException("REPL session has not been started.");
        }

        if (_process.HasExited)
        {
            throw new InvalidOperationException("REPL process has exited.");
        }
    }
}
