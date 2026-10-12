using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

internal sealed class HttpRuntimeFixture : IAsyncDisposable
{
    private readonly string _workspace;
    private readonly RuntimeServer _server;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _serverTask;

    private HttpRuntimeFixture(string workspace, RuntimeServer server, int port, HttpTestProvider provider)
    {
        _workspace = workspace;
        _server = server;
        _serverTask = server.RunAsync(_shutdown.Token);
        Address = new Uri($"http://localhost:{port}");
        Provider = provider;
    }

    public Uri Address { get; }
    public HttpTestProvider Provider { get; }

    public static async Task<HttpRuntimeFixture> StartAsync(bool configured = true)
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"madorin-http-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var provider = new HttpTestProvider();
        var server = await RuntimeServer.StartAsync(new RuntimeServerOptions(workspace)
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            PipePrefix = $"madorin.http.{Guid.NewGuid():N}",
            HandshakeSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            HttpPort = port,
            MemoryUserHome = Path.Combine(workspace, "home"),
            ProviderResolver = configured ? _ => provider : null
        });
        return new HttpRuntimeFixture(workspace, server, port, provider);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await _serverTask;
        }
        finally
        {
            await _server.DisposeAsync();
            _shutdown.Dispose();
            Directory.Delete(_workspace, recursive: true);
        }
    }

    internal sealed class HttpTestProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "test-provider";
        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);
        public IReadOnlyList<ProviderModel> Models { get; } = [new("test-model", "Test Model")];
        public List<RuntimeProviderRequest> Requests { get; } = [];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var text = request.RequestControl?.StopSequences is { Length: > 0 } stops
                ? "1 2 3 STOP 4 5 END 100"
                : Requests.Count == 1 ? "I will remember 42." : "The number was 42.";
            if (request.RequestControl?.StopSequences is { Length: > 0 } sequences)
            {
                var stop = sequences.Select(sequence => text.IndexOf(sequence, StringComparison.Ordinal))
                    .Where(index => index >= 0).DefaultIfEmpty(text.Length).Min();
                text = text[..stop];
            }
            yield return new TextDeltaProviderEvent(request.InvocationId, text);
            yield return new UsageUpdatedProviderEvent(request.InvocationId, 12, 4);
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
