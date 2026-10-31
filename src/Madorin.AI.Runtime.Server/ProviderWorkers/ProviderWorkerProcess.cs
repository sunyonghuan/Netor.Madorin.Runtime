using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

public sealed partial class ProviderWorkerProcess : IAsyncDisposable
{
    // Bound simultaneous worker startup so the hello handshake is not starved.
    private static readonly SemaphoreSlim StartupGate = new(4);
    private readonly string _directory;
    private readonly string _slug;
    private readonly string _protocol;
    private readonly string _runtimeVersion;
    private readonly ProviderWorkerProcessGroup _processGroup;
    private readonly bool _ownsProcessGroup;
    private readonly ProviderWorkerCrashFuse _fuse;
    private readonly Action<string>? _workerLog;
    private readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Channel<RuntimeProviderEvent>> _invokes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pings =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkerBlobInputLease> _blobInputs = new(StringComparer.Ordinal);
    private Process? _process;
    private Stream? _stdin;
    private CancellationTokenSource? _readerCts;
    private Task? _reader;
    private TaskCompletionSource<HelloFrameResult>? _hello;
    private ProviderCapabilities? _negotiatedCapabilities;
    private IReadOnlyList<ProviderModel>? _negotiatedModels;
    private bool _disposed;
    private int _stopping;
    private int _crashRecorded;

    public ProviderWorkerProcess(
        string directory,
        string slug,
        string protocol,
        string runtimeVersion)
        : this(directory, slug, protocol, runtimeVersion, processGroup: null, timeProvider: null, workerLog: null)
    {
    }

    internal ProviderWorkerProcess(
        string directory,
        string slug,
        string protocol,
        string runtimeVersion,
        ProviderWorkerProcessGroup? processGroup,
        TimeProvider? timeProvider,
        Action<string>? workerLog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        _directory = directory;
        _slug = slug;
        _protocol = protocol;
        _runtimeVersion = runtimeVersion;
        if (processGroup is null)
        {
            _processGroup = new ProviderWorkerProcessGroup();
            _ownsProcessGroup = true;
        }
        else
        {
            _processGroup = processGroup;
            _ownsProcessGroup = false;
        }

        _fuse = new ProviderWorkerCrashFuse(timeProvider);
        _workerLog = workerLog;
    }

    public string Slug => _slug;

    /// <summary>hello.ok 协商到的 Worker 能力声明；配置未写 capabilities 时供宿主投影使用。</summary>
    public ProviderCapabilities? NegotiatedCapabilities => _negotiatedCapabilities;

    /// <summary>Worker 已知目录，仅按精确模型 ID 补齐旧客户端缺少的限制。</summary>
    public IReadOnlyList<ProviderModel>? NegotiatedModels => _negotiatedModels;

    internal bool IsFused => _fuse.IsFused;

    internal int? ProcessId
    {
        get
        {
            try
            {
                return _process is { HasExited: false } process ? process.Id : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    internal bool IsAssignedToRuntimeGroup
    {
        get
        {
            try
            {
                return _process is { HasExited: false } process && _processGroup.Contains(process);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ShutdownAsync().ConfigureAwait(false);
        if (_ownsProcessGroup)
        {
            _processGroup.Dispose();
        }

        _gate.Dispose();
        _writeLock.Dispose();
    }
}

internal readonly record struct HelloFrameResult(
    bool Ok,
    string? Code,
    string? Message,
    ProviderCapabilities? Capabilities = null);
