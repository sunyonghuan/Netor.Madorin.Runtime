using System.Diagnostics;
using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

public sealed partial class ProviderWorkerProcess
{
    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fuse.IsFused)
        {
            throw CreateFusedException();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_fuse.IsFused)
            {
                throw CreateFusedException();
            }

            if (_process is { HasExited: false } && await TryPingAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (_process is not null)
            {
                RecordUnexpectedCrash();
                await KillAsync().ConfigureAwait(false);
                if (_fuse.IsFused)
                {
                    throw CreateFusedException();
                }
            }

            await StartupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StartAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                StartupGate.Release();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var exe = ProviderWorkerPaths.GetExecutablePath(_directory, _slug);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException(
                $"Provider worker not found for protocol '{_protocol}' slug '{_slug}' in '{_directory}'.",
                exe);
        }

        var start = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = _directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--slug");
        start.ArgumentList.Add(_slug);
        start.Environment["DOTNET_EnableDiagnostics"] = "0";

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
            {
                WriteWorkerLog(RedactSecrets(args.Data));
            }
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start provider worker '{exe}'.");
        }

        process.BeginErrorReadLine();
        _crashRecorded = 0;
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _hello = new TaskCompletionSource<HelloFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _readerCts = new CancellationTokenSource();
        _reader = ReadLoopAsync(process.StandardOutput.BaseStream, _readerCts.Token);
        _processGroup.Assign(process);

        await WriteFrameAsync(
                new HelloFrame(ProviderWorkerIpc.Version, _protocol, _slug, _runtimeVersion),
                cancellationToken)
            .ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        HelloFrameResult hello;
        try
        {
            hello = await _hello.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RecordUnexpectedCrash();
            FailAll($"Worker '{_slug}' hello timed out.");
            await KillAsync().ConfigureAwait(false);
            throw new TimeoutException($"Worker '{_slug}' hello timed out.");
        }

        if (!hello.Ok)
        {
            RecordUnexpectedCrash();
            FailAll($"Worker '{_slug}' rejected hello ({hello.Code}): {hello.Message}");
            await KillAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Worker '{_slug}' rejected hello ({hello.Code}): {hello.Message}");
        }
    }

    private InvalidOperationException CreateFusedException() =>
        new($"Provider worker '{_slug}' is fused after repeated crashes.");
}
