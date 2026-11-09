using System.Collections.Concurrent;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.WorkerHost;

/// <summary>
/// stdin/stdout Worker loop. Skills stay null; tools arrive on <see cref="RuntimeProviderRequest.Tools"/>.
/// </summary>
public static partial class ProviderWorkerHost
{
    public static Task<int> RunAsync(
        string protocol,
        string slug,
        Func<ProviderConnectionConfig, IRuntimeProviderAdapter> adapterFactory,
        Stream stdin,
        Stream stdout,
        TextWriter stderr,
        ProviderCapabilities? declaredCapabilities = null,
        CancellationToken cancellationToken = default) => RunAsync(
            protocol, slug, adapterFactory, stdin, stdout, stderr,
            declaredCapabilities, declaredModels: null, cancellationToken);

    public static async Task<int> RunAsync(
        string protocol,
        string slug,
        Func<ProviderConnectionConfig, IRuntimeProviderAdapter> adapterFactory,
        Stream stdin,
        Stream stdout,
        TextWriter stderr,
        ProviderCapabilities? declaredCapabilities,
        IReadOnlyList<ProviderModel>? declaredModels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapterFactory);
        return await RunWithBlobResolverAsync(protocol, slug, (config, _) => adapterFactory(config),
            stdin, stdout, stderr, declaredCapabilities, declaredModels, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> RunWithBlobResolverAsync(
        string protocol,
        string slug,
        Func<ProviderConnectionConfig, IProviderBlobResolver, IRuntimeProviderAdapter> adapterFactory,
        Stream stdin,
        Stream stdout,
        TextWriter stderr,
        ProviderCapabilities? declaredCapabilities = null,
        IReadOnlyList<ProviderModel>? declaredModels = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(adapterFactory);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        using var session = new Session(
            protocol,
            slug,
            adapterFactory,
            stdin,
            stdout,
            stderr,
            declaredCapabilities,
            declaredModels,
            cancellationToken);
        return await session.RunAsync().ConfigureAwait(false);
    }

    private sealed partial class Session(
        string protocol,
        string slug,
        Func<ProviderConnectionConfig, IProviderBlobResolver, IRuntimeProviderAdapter> adapterFactory,
        Stream stdin,
        Stream stdout,
        TextWriter stderr,
        ProviderCapabilities? declaredCapabilities,
        IReadOnlyList<ProviderModel>? declaredModels,
        CancellationToken cancellationToken) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _invocations =
            new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _shutdownCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private bool _helloCompleted;
        private readonly ConcurrentDictionary<string, WorkerInputBlobResolver> _blobResolvers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, WorkerOutputBlobWriter> _blobWriters = new(StringComparer.Ordinal);

        public async Task<int> RunAsync()
        {
            try
            {
                while (!_shutdownCts.IsCancellationRequested)
                {
                    var read = await ProviderWorkerFrameCodec.ReadAsync(stdin, _shutdownCts.Token)
                        .ConfigureAwait(false);
                    if (read.Status == ProviderWorkerFrameReadStatus.EndOfStream)
                    {
                        CancelAllInvocations();
                        return _helloCompleted ? 0 : 1;
                    }

                    if (read.Status == ProviderWorkerFrameReadStatus.Oversize)
                    {
                        await WriteAsync(
                                new ErrorFrame(
                                    null,
                                    ProviderWorkerIpc.ErrorWorkerCrashed,
                                    $"Frame length {read.DeclaredLength} exceeds {ProviderWorkerIpc.MaxFrameLength}."),
                                _shutdownCts.Token)
                            .ConfigureAwait(false);
                        return 2;
                    }

                    var exit = await DispatchAsync(read.Frame!).ConfigureAwait(false);
                    if (exit is not null)
                    {
                        return exit.Value;
                    }
                }

                return 0;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || _shutdownCts.IsCancellationRequested)
            {
                return 0;
            }
        }

        public void Dispose()
        {
            _shutdownCts.Dispose();
            _writeLock.Dispose();
        }

        private Task WriteAsync(ProviderWorkerFrame frame, CancellationToken ct) =>
            WriteLockedAsync(stdout, _writeLock, frame, ct);
    }

    private static async Task WriteLockedAsync(
        Stream stdout,
        SemaphoreSlim writeLock,
        ProviderWorkerFrame frame,
        CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProviderWorkerFrameCodec.WriteAsync(stdout, frame, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
