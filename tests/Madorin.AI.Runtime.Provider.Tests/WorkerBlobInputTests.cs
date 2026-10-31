using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;
using Madorin.AI.Runtime.Server.ProviderWorkers;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class WorkerBlobInputTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("OpenAI", "openai")]
    [DataRow("Anthropic", "anthropic")]
    [DataRow("OpenAI Compatible", "openai-compatible")]
    [DataRow("Azure OpenAI", "azure-openai")]
    [DataRow("Aliyun", "aliyun")]
    [DataRow("Gemini", "gemini")]
    [DataRow("GLM", "glm")]
    [DataRow("Ollama", "ollama")]
    [DataRow("Kimi", "kimi")]
    [DataRow("DeepSeek", "deepseek")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllWorkers_ResolveToolImageWithNativeHttpPart(string protocol, string slug)
    {
        var kind = slug == "openai" ? ProviderAdapterKind.OpenAI
            : slug == "anthropic" ? ProviderAdapterKind.Anthropic : ProviderAdapterKind.OpenAICompatible;
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind), TestContext.CancellationToken);
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var connection = new ProviderConnectionConfig("worker-blob", protocol,
            new Uri(server.BaseAddress, "v1").AbsoluteUri, ProviderAdapterTestFactory.ApiKey,
            Models: [ProviderAdapterTestFactory.ModelId],
            Capabilities: new(Streaming: true, Reasoning: true));
        var bytes = RandomNumberGenerator.GetBytes(300_001);
        var blob = Reference(bytes);
        using var args = JsonDocument.Parse("{}");
        var opens = 0;
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            ProviderId = connection.ProviderId,
            InvocationId = Guid.NewGuid().ToString("N"),
            Messages = [new("user", [new TextContentBlock("inspect")]),
                new("assistant", [new ToolCallContentBlock("c", "attach", "attach", args.RootElement.Clone())]),
                new("tool", [new ToolResultContentBlock("c", "attach", true, [new BlobRefContentBlock(blob)])])],
            OpenBlobRead = (reference, ct) =>
            {
                Assert.AreEqual(blob, reference);
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref opens);
                return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
            }
        };
        var events = await ProviderAdapterTestFactory.CollectAsync(
            supervisor.CreateAdapter(protocol, connection), request, TestContext.CancellationToken);
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.AreEqual(1, opens);
        using var body = JsonDocument.Parse(Assert.ContainsSingle(server.Requests).Body);
        var encoded = FindImage(body.RootElement);
        Assert.IsNotNull(encoded);
        if (encoded.StartsWith("data:", StringComparison.Ordinal))
        {
            encoded = encoded[(encoded.IndexOf(',') + 1)..];
        }
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(encoded));
        Assert.DoesNotContain(blob.BlobId, server.Requests[0].Body, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("generation")]
    [DataRow("scope")]
    [DataRow("hash")]
    [DataRow("offset")]
    [DataRow("count")]
    [DataRow("quota")]
    public async Task Lease_RejectsForgedOrOutOfBoundsReadBeforeOpening(string problem)
    {
        byte[] bytes = [1, 2, 3];
        var blob = Reference(bytes);
        var opened = false;
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new("user", [new BlobRefContentBlock(blob)])],
            MaximumBufferedBlobBytes = problem == "quota" ? 2 : 100,
            OpenBlobRead = (_, _) => { opened = true; return Task.FromResult<Stream>(new MemoryStream(bytes)); }
        };
        await using var lease = new WorkerBlobInputLease(request, "generation", TestContext.CancellationToken);
        var read = new BlobReadFrame(request.InvocationId, problem == "generation" ? "old" : "generation", "t",
            problem == "scope" ? blob with { AccessScope = "other-run" }
                : problem == "hash" ? blob with { Sha256 = new string('0', 64) } : blob,
            problem == "offset" ? 1 : 0, problem == "count" ? 4 : 3);
        await Assert.ThrowsAsync<InvalidDataException>(() => lease.ReadAsync(read));
        Assert.IsFalse(opened);
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(17 * 1024 * 1024, false)]
    [DataRow(4, true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Worker_HandlesEmptyLargeAndCorruptBlob(int size, bool badHash)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible), TestContext.CancellationToken);
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var connection = new ProviderConnectionConfig("p", "OpenAI Compatible",
            new Uri(server.BaseAddress, "v1").AbsoluteUri, "test", Models: [ProviderAdapterTestFactory.ModelId]);
        var bytes = new byte[size];
        var blob = Reference(bytes);
        if (badHash)
        {
            blob = blob with { Sha256 = new string('f', 64) };
        }
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new("user", [new BlobRefContentBlock(blob)])],
            OpenBlobRead = (_, _) => Task.FromResult<Stream>(new MemoryStream(bytes))
        };
        var events = await ProviderAdapterTestFactory.CollectAsync(
            supervisor.CreateAdapter(connection.Protocol, connection), request, TestContext.CancellationToken);
        if (badHash)
        {
            Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
            Assert.IsEmpty(server.Requests);
        }
        else
        {
            Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
            using var json = JsonDocument.Parse(Assert.ContainsSingle(server.Requests).Body);
            var encoded = FindImage(json.RootElement)!;
            CollectionAssert.AreEqual(bytes, Convert.FromBase64String(encoded[(encoded.IndexOf(',') + 1)..]));
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Worker_ConcurrentAndRepeatedInvocationReadsStayIsolated()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible), TestContext.CancellationToken);
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var connection = new ProviderConnectionConfig("p", "OpenAI Compatible",
            new Uri(server.BaseAddress, "v1").AbsoluteUri, "test", Models: [ProviderAdapterTestFactory.ModelId]);
        var adapter = supervisor.CreateAdapter(connection.Protocol, connection);
        var all = Enumerable.Range(0, 10).Select(async index =>
        {
            var bytes = Enumerable.Repeat((byte)index, 150_001).ToArray();
            var blob = Reference(bytes);
            for (var round = 0; round < 2; round++)
            {
                var request = ProviderAdapterTestFactory.CreateRequest() with
                {
                    InvocationId = $"parallel-{index}",
                    Messages = [new("user", [new BlobRefContentBlock(blob)])],
                    OpenBlobRead = (reference, _) =>
                    {
                        Assert.AreEqual(blob, reference);
                        return Task.FromResult<Stream>(new MemoryStream(bytes));
                    }
                };
                var events = await ProviderAdapterTestFactory.CollectAsync(adapter, request, TestContext.CancellationToken);
                Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
                Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
            }
        });
        await Task.WhenAll(all);
        Assert.HasCount(20, server.Requests);
        var seen = new Dictionary<byte, int>();
        foreach (var request in server.Requests)
        {
            using var json = JsonDocument.Parse(request.Body);
            var encoded = FindImage(json.RootElement)!;
            var bytes = Convert.FromBase64String(encoded[(encoded.IndexOf(',') + 1)..]);
            Assert.IsTrue(bytes.All(value => value == bytes[0]));
            seen[bytes[0]] = seen.GetValueOrDefault(bytes[0]) + 1;
        }
        Assert.HasCount(10, seen);
        Assert.IsTrue(seen.Values.All(count => count == 2));
    }

    [TestMethod]
    public async Task Worker_PreservesHistoryBoundaryForReasoningAndBlobProjection()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible), TestContext.CancellationToken);
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var connection = new ProviderConnectionConfig("p", "Aliyun", new Uri(server.BaseAddress, "v1").AbsoluteUri,
            "test", Models: [ProviderAdapterTestFactory.ModelId]);
        byte[] bytes = [1, 2, 3];
        var blob = Reference(bytes);
        using var args = JsonDocument.Parse("{}");
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new("user", [new TextContentBlock("inspect")]),
                new("assistant", [new ReasoningContentBlock("historical thinking"),
                    new ToolCallContentBlock("c", "attach", "attach", args.RootElement.Clone())]),
                new("tool", [new ToolResultContentBlock("c", "attach", true, [new BlobRefContentBlock(blob)])])],
            HistoryMessageCount = 3,
            OpenBlobRead = (_, _) => Task.FromResult<Stream>(new MemoryStream(bytes))
        };
        var adapter = supervisor.CreateAdapter(connection.Protocol, connection);
        var events = await ProviderAdapterTestFactory.CollectAsync(adapter, request, TestContext.CancellationToken);
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        var body = Assert.ContainsSingle(server.Requests).Body;
        Assert.DoesNotContain("historical thinking", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        Assert.IsNotNull(FindImage(document.RootElement));

        var current = await ProviderAdapterTestFactory.CollectAsync(adapter,
            request with { InvocationId = "current-reasoning", HistoryMessageCount = 0 }, TestContext.CancellationToken);
        Assert.ContainsSingle(current.OfType<InvocationFailedProviderEvent>());
        Assert.ContainsSingle(server.Requests);
    }

    [TestMethod]
    public async Task Lease_CancellationInterruptsReadAndDisposesSource()
    {
        var blob = Reference([1, 2, 3]);
        using var cancel = new CancellationTokenSource();
        var source = new PendingReadStream();
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new("user", [new BlobRefContentBlock(blob)])],
            OpenBlobRead = (_, _) => Task.FromResult<Stream>(source)
        };
        var lease = new WorkerBlobInputLease(request, "g", cancel.Token);
        var read = lease.ReadAsync(new BlobReadFrame(request.InvocationId, "g", "t", blob, 0, 3));
        await source.Started.Task.WaitAsync(TestContext.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await read);
        await lease.DisposeAsync();
        Assert.IsTrue(source.WasDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.ReadAsync(
            new BlobReadFrame(request.InvocationId, "g", "late", blob, 0, 3)));
    }

    [TestMethod]
    public async Task Worker_CancellingBlobReadAllowsNextInvocation()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible), TestContext.CancellationToken);
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var connection = new ProviderConnectionConfig("p", "OpenAI Compatible",
            new Uri(server.BaseAddress, "v1").AbsoluteUri, "test", Models: [ProviderAdapterTestFactory.ModelId]);
        var adapter = supervisor.CreateAdapter(connection.Protocol, connection);
        var source = new PendingReadStream();
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new("user", [new BlobRefContentBlock(Reference([1, 2, 3]))])],
            OpenBlobRead = (_, _) => Task.FromResult<Stream>(source)
        };
        using var cancel = new CancellationTokenSource();
        var collecting = ProviderAdapterTestFactory.CollectAsync(adapter, request, cancel.Token);
        await source.Started.Task.WaitAsync(TestContext.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await collecting);
        Assert.IsTrue(source.WasDisposed);
        Assert.IsEmpty(server.Requests);
        var next = await ProviderAdapterTestFactory.CollectAsync(adapter,
            ProviderAdapterTestFactory.CreateRequest() with { InvocationId = "next" }, TestContext.CancellationToken);
        Assert.ContainsSingle(next.OfType<InvocationCompletedProviderEvent>());
    }

    private sealed class PendingReadStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    [TestMethod]
    public async Task Frames_KeepInvokeSmallAndChunkBounded()
    {
        var bytes = new byte[17 * 1024 * 1024];
        var blob = Reference(bytes);
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new("user", [new BlobRefContentBlock(blob)])],
            OpenBlobRead = (_, _) => Task.FromResult<Stream>(new MemoryStream(bytes))
        };
        await using var frame = new MemoryStream();
        await ProviderWorkerFrameCodec.WriteAsync(frame, new InvokeFrame("i",
            new ProviderConnectionConfig("p", "OpenAI", "https://example.test", "test"), request, "g"));
        Assert.IsLessThan(3000L, frame.Length);
        frame.SetLength(0);
        await ProviderWorkerFrameCodec.WriteAsync(frame,
            new BlobChunkFrame("i", "g", "t", 0, new byte[ProviderWorkerIpc.MaxBlobChunkBytes], false));
        Assert.IsLessThan((long)ProviderWorkerIpc.MaxBlobChunkFrameBytes, frame.Length);
        frame.Position = 0;
        var restored = (BlobChunkFrame)(await ProviderWorkerFrameCodec.ReadAsync(frame)).Frame!;
        Assert.HasCount(ProviderWorkerIpc.MaxBlobChunkBytes, restored.Data);
    }

    private static BlobReference Reference(byte[] bytes) => new("private-test-blob", bytes.Length,
        Convert.ToHexString(SHA256.HashData(bytes)), "image/png", "run:test", DateTimeOffset.UtcNow.AddHours(1));

    private static string? FindImage(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("image_url", out var image))
            {
                return image.ValueKind == JsonValueKind.String ? image.GetString() : image.GetProperty("url").GetString();
            }
            if (value.TryGetProperty("type", out var type) && type.GetString() == "image")
            {
                return value.GetProperty("source").GetProperty("data").GetString();
            }
            foreach (var property in value.EnumerateObject())
            {
                if (FindImage(property.Value) is { } found)
                {
                    return found;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                if (FindImage(child) is { } found)
                {
                    return found;
                }
            }
        }
        return null;
    }
}
