using System.Security.Cryptography;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Server.ProviderWorkers;
using Madorin.AI.Runtime.Modes.Expert;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class WorkerBlobOutputTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("OpenAI Compatible", 64 * 1024 * 1024)]
    [DataRow("Azure OpenAI", 64 * 1024 * 1024)]
    [DataRow("Aliyun", 64 * 1024 * 1024)]
    [DataRow("Gemini", 64 * 1024 * 1024)]
    [DataRow("GLM", 64 * 1024 * 1024)]
    [DataRow("Ollama", 64 * 1024 * 1024)]
    [DataRow("Kimi", 64 * 1024 * 1024)]
    [DataRow("DeepSeek", 64 * 1024 * 1024)]
    [DataRow("OpenAI Compatible", 300_006)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ChatWorkers_StoreReturnedAudioViaBoundedChunks(string protocol, int maximumBytes)
    {
        var root = Path.Join(Path.GetTempPath(), "madorin-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(300_001);
            var first = Convert.ToBase64String(bytes.AsSpan(0, 100_001));
            var second = Convert.ToBase64String(bytes.AsSpan(100_001));
            var payload = "data: {\"id\":\"audio-response\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"before\"},\"finish_reason\":null}]}\n\n"
                + "data: {\"id\":\"audio-response\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"audio\":{\"id\":\"a\",\"data\":\"" + first + "\"}},\"finish_reason\":null}]}\n\n"
                + "data: {\"id\":\"audio-response\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"audio\":{\"data\":\"" + second + "\"}},\"finish_reason\":null}]}\n\n"
                + "data: {\"id\":\"audio-response\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"after\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            await using var server = await FakeProviderServer.StartAsync(
                _ => FakeProviderResponse.EventStream(new FakeProviderChunk(payload)), TestContext.CancellationToken);
            await using var store = new BlobStagingStore(root, new RuntimeLimits(), 64 * 1024 * 1024);
            await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
            var connection = new ProviderConnectionConfig("output-test", protocol,
                new Uri(server.BaseAddress, "v1").AbsoluteUri, "test", Models: [ProviderAdapterTestFactory.ModelId]);
            var request = ProviderAdapterTestFactory.CreateRequest() with
            {
                InvocationId = Guid.NewGuid().ToString("N"),
                MaximumBufferedBlobBytes = maximumBytes,
                StoreOutputBlob = (stream, mime, length, hash, ct) => store.StoreAsync("run", stream, mime,
                    "run:run", DateTimeOffset.UtcNow.AddHours(1), length, hash, ct)
            };
            var events = await ProviderAdapterTestFactory.CollectAsync(
                supervisor.CreateAdapter(protocol, connection), request, TestContext.CancellationToken);
            Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>(),
                string.Join(" | ", events.OfType<InvocationFailedProviderEvent>().Select(e => e.Error.Message)));
            var attachment = Assert.ContainsSingle(events.OfType<AttachmentCompletedProviderEvent>());
            Assert.AreEqual(bytes.LongLength, attachment.Blob.Length);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), attachment.Blob.Sha256);
            var restored = await store.ReadAsync(attachment.Blob, bytes.Length, TestContext.CancellationToken);
            CollectionAssert.AreEqual(bytes, restored.ToArray());
            var visible = events.Where(e => e is TextDeltaProviderEvent or AttachmentCompletedProviderEvent).ToArray();
            Assert.IsInstanceOfType<TextDeltaProviderEvent>(visible[0]);
            Assert.IsInstanceOfType<AttachmentCompletedProviderEvent>(visible[1]);
            Assert.IsInstanceOfType<TextDeltaProviderEvent>(visible[2]);
            var markdown = await ProviderAttachmentDelivery.SaveAsync(attachment, store, root,
                TestContext.CancellationToken);
            Assert.Contains(".attach/", markdown.Replace('\\', '/'), StringComparison.Ordinal);
            var saved = Assert.ContainsSingle(Directory.GetFiles(Path.Join(root, ".attach")));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(saved, TestContext.CancellationToken));
            Assert.IsEmpty(Directory.GetFiles(Path.Join(root, "staging")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    [DataRow(300_001)]
    [DataRow(300_000)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task OpenAIWorker_CompletedImageOutputStoresBlobAndMarkdownWithUniqueNames(int maximumBytes)
    {
        var root = Path.Join(Path.GetTempPath(), "madorin-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(300_001);
            var payload = "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"ig\",\"type\":\"image_generation_call\",\"status\":\"completed\",\"result\":\""
                + Convert.ToBase64String(bytes) + "\"}}\n\n";
            var normal = ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAI);
            await using var server = await FakeProviderServer.StartAsync(
                _ => FakeProviderResponse.EventStream([new FakeProviderChunk(payload), .. normal.Chunks]),
                TestContext.CancellationToken);
            await using var store = new BlobStagingStore(root, new RuntimeLimits(), 64 * 1024 * 1024);
            await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
            var connection = new ProviderConnectionConfig("p", "OpenAI", new Uri(server.BaseAddress, "v1").AbsoluteUri,
                "test", Models: [ProviderAdapterTestFactory.ModelId]);
            var request = ProviderAdapterTestFactory.CreateRequest() with
            {
                MaximumBufferedBlobBytes = maximumBytes,
                StoreOutputBlob = (source, mime, length, hash, ct) => store.StoreAsync("run", source, mime,
                    "run:run", DateTimeOffset.UtcNow.AddHours(1), length, hash, ct)
            };
            var events = await ProviderAdapterTestFactory.CollectAsync(supervisor.CreateAdapter("OpenAI", connection),
                request, TestContext.CancellationToken);
            if (maximumBytes < bytes.Length)
            {
                Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
                Assert.IsEmpty(events.OfType<AttachmentCompletedProviderEvent>());
                Assert.IsEmpty(Directory.GetFiles(Path.Join(root, "blobs")));
                return;
            }
            Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>(),
                string.Join(" | ", events.OfType<InvocationFailedProviderEvent>().Select(e => e.Error.Message)));
            var attachment = Assert.ContainsSingle(events.OfType<AttachmentCompletedProviderEvent>());
            Assert.AreEqual("image/png", attachment.Blob.ContentType);
            for (var i = 0; i < 2; i++)
            {
                var markdown = await ProviderAttachmentDelivery.SaveAsync(attachment, store, root,
                    TestContext.CancellationToken);
                Assert.StartsWith("\n\n![", markdown, StringComparison.Ordinal);
                Assert.Contains("file:///", markdown, StringComparison.Ordinal);
            }
            var files = Directory.GetFiles(Path.Join(root, ".attach"));
            Assert.HasCount(2, files);
            Assert.AreNotEqual(files[0], files[1]);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(files[0], TestContext.CancellationToken));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    [DataRow("generation")]
    [DataRow("hash")]
    [DataRow("offset")]
    [DataRow("quota")]
    [DataRow("cancel")]
    public async Task OutputLease_InvalidOrCancelledTransferPublishesNothing(string problem)
    {
        var root = Path.Join(Path.GetTempPath(), "madorin-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = new BlobStagingStore(root, new RuntimeLimits(), 64 * 1024 * 1024);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            var request = ProviderAdapterTestFactory.CreateRequest() with
            {
                MaximumBufferedBlobBytes = problem == "quota" ? 2 : 1024,
                StoreOutputBlob = (source, mime, length, hash, ct) => store.StoreAsync("run", source, mime,
                    "run:run", DateTimeOffset.UtcNow.AddHours(1), length, hash, ct)
            };
            await using (var lease = new WorkerBlobOutputLease(request, "g", cancel.Token))
            {
                byte[] bytes = [1, 2, 3];
                var transfer = Guid.NewGuid().ToString("N");
                var begin = new BlobWriteFrame(request.InvocationId, problem == "generation" ? "old" : "g",
                    transfer, "begin", 0, [], "application/pdf", 3,
                    problem == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)));
                try
                {
                    await lease.WriteAsync(begin);
                    if (problem == "cancel")
                    {
                        await cancel.CancelAsync();
                    }
                    await lease.WriteAsync(new(request.InvocationId, "g", transfer, "chunk",
                        problem == "offset" ? 1 : 0, bytes));
                    await lease.WriteAsync(new(request.InvocationId, "g", transfer, "complete", 3, []));
                    Assert.Fail("Invalid output must fail.");
                }
                catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException)
                {
                }
            }
            Assert.IsEmpty(Directory.GetFiles(Path.Join(root, "staging")));
            Assert.IsEmpty(Directory.GetFiles(Path.Join(root, "blobs")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task OutputLease_CompletedAttachmentsShareInvocationQuota()
    {
        byte[] bytes = [1, 2, 3];
        var stored = 0;
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            MaximumBufferedBlobBytes = 5,
            StoreOutputBlob = async (source, mime, length, hash, ct) =>
            {
                await source.CopyToAsync(Stream.Null, ct);
                stored++;
                return new BlobReference("confirmed", length, hash, mime, "run:test", DateTimeOffset.UtcNow.AddHours(1));
            }
        };
        await using var lease = new WorkerBlobOutputLease(request, "g", TestContext.CancellationToken);
        var transfer = Guid.NewGuid().ToString("N");
        var begin = new BlobWriteFrame(request.InvocationId, "g", transfer, "begin", 0, [], "application/pdf",
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        await lease.WriteAsync(begin);
        await lease.WriteAsync(new(request.InvocationId, "g", transfer, "chunk", 0, bytes));
        var ack = await lease.WriteAsync(new(request.InvocationId, "g", transfer, "complete", bytes.Length, []));
        Assert.IsNotNull(ack.Blob);
        Assert.IsTrue(lease.Contains(ack.Blob));
        await Assert.ThrowsAsync<InvalidDataException>(() => lease.WriteAsync(begin with
        {
            TransferId = Guid.NewGuid().ToString("N")
        }));
        Assert.AreEqual(1, stored);
    }

    [TestMethod]
    public async Task OutputFrames_RoundTripReferencesAndRejectOversizeChunks()
    {
        using var stream = new MemoryStream();
        var transfer = Guid.NewGuid().ToString("N");
        var frame = new BlobWriteFrame("i", "g", transfer, "chunk", 0,
            RandomNumberGenerator.GetBytes(ProviderWorkerIpc.MaxBlobChunkBytes));
        await ProviderWorkerFrameCodec.WriteAsync(stream, frame, TestContext.CancellationToken);
        Assert.IsLessThan(ProviderWorkerIpc.MaxBlobChunkFrameBytes + 4, stream.Length);
        stream.Position = 0;
        var read = await ProviderWorkerFrameCodec.ReadAsync(stream, TestContext.CancellationToken);
        var restored = (BlobWriteFrame)read.Frame!;
        Assert.AreEqual(frame.Generation, restored.Generation);
        CollectionAssert.AreEqual(frame.Data, restored.Data);
        await Assert.ThrowsAsync<InvalidDataException>(() => ProviderWorkerFrameCodec.WriteAsync(stream,
            frame with { Data = new byte[ProviderWorkerIpc.MaxBlobChunkBytes + 1] }, TestContext.CancellationToken));
    }

    private static void Cleanup(string root)
    {
        var absolute = Path.GetFullPath(root);
        if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected test cleanup directory.");
        }
        Directory.Delete(absolute, recursive: true);
    }
}
