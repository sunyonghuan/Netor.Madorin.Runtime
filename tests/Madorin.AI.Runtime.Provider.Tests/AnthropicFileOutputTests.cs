using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Anthropic;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Server.ProviderWorkers;
using Netor.Anthropic.Minimal;
using Netor.Anthropic.Minimal.Extensions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class AnthropicFileOutputTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("direct", "bash_code_execution_tool_result", "text/plain")]
    [DataRow("worker", "bash_code_execution_tool_result", "text/plain")]
    [DataRow("worker", "code_execution_tool_result", "text/plain")]
    [DataRow("worker", "container_upload", "text/plain")]
    [DataRow("worker", "bash_code_execution_tool_result", "audio/wav")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task GeneratedFiles_AreDownloadedStoredAndDeliveredInOrder(string route, string blockType, string mime)
    {
        var root = Path.Join(Path.GetTempPath(), "anthropic-file-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = new string('a', 300_001);
            var bytes = Encoding.UTF8.GetBytes(payload);
            await using var server = await FakeProviderServer.StartAsync(request => request.Method == "POST"
                ? FakeProviderResponse.EventStream(new FakeProviderChunk(CreateStream(blockType)))
                : request.Path.EndsWith("/content", StringComparison.Ordinal)
                    ? new(200, mime, [new(payload)])
                    : FakeProviderResponse.Error(200, Metadata(request.Path.Split('/')[^1], bytes.Length)
                        .Replace("text/plain", mime, StringComparison.Ordinal)),
                TestContext.CancellationToken);
            await using var store = new BlobStagingStore(root, new RuntimeLimits(), 64 * 1024 * 1024);
            await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
            using var direct = new AnthropicProviderAdapter("p", "test-secret", server.BaseAddress.AbsoluteUri, models: null);
            var provider = route == "worker"
                ? supervisor.CreateAdapter("Anthropic", new("p", "Anthropic", server.BaseAddress.AbsoluteUri,
                    "test-secret", Models: [ProviderAdapterTestFactory.ModelId]))
                : (IRuntimeProviderAdapter)direct;
            var request = ProviderAdapterTestFactory.CreateRequest() with
            {
                StoreOutputBlob = (source, mime, length, hash, ct) => store.StoreAsync("run", source, mime,
                    "run:run", DateTimeOffset.UtcNow.AddHours(1), length, hash, ct)
            };
            var events = await ProviderAdapterTestFactory.CollectAsync(provider, request, TestContext.CancellationToken);
            Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>(),
                string.Join(" | ", events.OfType<InvocationFailedProviderEvent>().Select(e => e.Error.Message)));
            Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
            Assert.IsEmpty(events.OfType<ToolCallCompleteProviderEvent>());
            var attachments = events.OfType<AttachmentCompletedProviderEvent>().ToArray();
            Assert.HasCount(blockType == "container_upload" ? 1 : 2, attachments);
            foreach (var attachment in attachments)
            {
                var restored = await store.ReadAsync(attachment.Blob, bytes.Length, TestContext.CancellationToken);
                CollectionAssert.AreEqual(bytes, restored.ToArray());
                var markdown = await ProviderAttachmentDelivery.SaveAsync(attachment, store, root,
                    TestContext.CancellationToken);
                Assert.StartsWith("\n\n[", markdown, StringComparison.Ordinal);
                Assert.Contains(mime == "audio/wav" ? ".wav](file:///" : ".txt](file:///", markdown, StringComparison.Ordinal);
            }
            var visible = events.Where(e => e is TextDeltaProviderEvent or AttachmentCompletedProviderEvent).ToArray();
            Assert.AreEqual("before", ((TextDeltaProviderEvent)visible[0]).Delta);
            Assert.AreEqual("after", ((TextDeltaProviderEvent)visible[^1]).Delta);
            Assert.IsTrue(visible[1..^1].All(e => e is AttachmentCompletedProviderEvent));
            var gets = server.Requests.Where(r => r.Method == "GET").ToArray();
            Assert.HasCount(attachments.Length * 2, gets);
            foreach (var get in gets)
            {
                Assert.AreEqual("test-secret", get.Headers["x-api-key"]);
                Assert.AreEqual("files-api-2025-04-14", get.Headers["anthropic-beta"]);
                Assert.IsFalse(get.Headers.ContainsKey("Authorization"));
            }
            Assert.IsEmpty(Directory.GetFiles(Path.Join(root, "staging")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    [DataRow("unavailable")]
    [DataRow("quota")]
    [DataRow("length")]
    [DataRow("mime")]
    [DataRow("redirect")]
    [DataRow("status")]
    [DataRow("bad-id")]
    [DataRow("incomplete")]
    [DataRow("injected")]
    [DataRow("cancel")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task InvalidFiles_DoNotPublishAttachments(string problem)
    {
        var root = Path.Join(Path.GetTempPath(), "anthropic-file-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var other = await FakeProviderServer.StartAsync(_ => FakeProviderResponse.Error(200, "stolen"),
                TestContext.CancellationToken);
            var stream = CreateStream("container_upload", problem == "bad-id" ? "../escape" : "file_one");
            if (problem == "incomplete")
            {
                stream = stream.Replace("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n",
                    "", StringComparison.Ordinal);
            }
            await using var server = await FakeProviderServer.StartAsync(request =>
            {
                if (request.Method == "POST")
                {
                    return FakeProviderResponse.EventStream(new FakeProviderChunk(stream));
                }
                if (problem == "redirect")
                {
                    return FakeProviderResponse.Error(302, "", new Dictionary<string, string>
                    {
                        ["Location"] = new Uri(other.BaseAddress, "stolen").AbsoluteUri
                    });
                }
                if (problem == "status")
                {
                    return FakeProviderResponse.Error(403, "denied");
                }
                if (request.Path.EndsWith("/content", StringComparison.Ordinal))
                {
                    return new FakeProviderResponse(200, problem == "mime" ? "image/png" : "text/plain",
                        [new("abc", problem == "cancel" ? TimeSpan.FromSeconds(5) : default)]);
                }
                return FakeProviderResponse.Error(200, Metadata("file_one", problem == "length" ? 4 : 3,
                    problem != "unavailable"));
            }, TestContext.CancellationToken);
            await using var store = new BlobStagingStore(root, new RuntimeLimits(), 64 * 1024 * 1024);
            using var client = new AnthropicMinimalClient(new()
            {
                BaseUrl = server.BaseAddress.AbsoluteUri, ApiKey = "test-secret"
            });
            using var adapter = problem == "injected"
                ? new AnthropicProviderAdapter(client.AsIChatClient(ProviderAdapterTestFactory.ModelId))
                : new AnthropicProviderAdapter("p", "test-secret", server.BaseAddress.AbsoluteUri, models: null);
            var request = ProviderAdapterTestFactory.CreateRequest() with
            {
                MaximumBufferedBlobBytes = problem == "quota" ? 2 : 1024,
                StoreOutputBlob = (source, mime, length, hash, ct) => store.StoreAsync("run", source, mime,
                    "run:run", DateTimeOffset.UtcNow.AddHours(1), length, hash, ct)
            };
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            if (problem == "cancel")
            {
                stop.CancelAfter(TimeSpan.FromMilliseconds(300));
            }
            var events = new List<RuntimeProviderEvent>();
            try
            {
                await foreach (var item in adapter.CompleteStreamingAsync(request, stop.Token))
                {
                    events.Add(item);
                }
                Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
            }
            catch (OperationCanceledException) when (problem == "cancel" && stop.IsCancellationRequested)
            {
            }
            Assert.IsEmpty(events.OfType<AttachmentCompletedProviderEvent>());
            Assert.IsEmpty(events.OfType<InvocationCompletedProviderEvent>());
            Assert.IsEmpty(other.Requests);
            Assert.IsEmpty(Directory.GetFiles(Path.Join(root, "staging")));
            Assert.IsEmpty(Directory.GetFiles(Path.Join(root, "blobs")));
            if (problem is "bad-id" or "incomplete" or "injected")
            {
                Assert.IsEmpty(server.Requests.Where(r => r.Method == "GET"));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    internal static string CreateStream(string type, string fileId = "file_one")
    {
        var block = type == "container_upload"
            ? $$"""{"type":"container_upload","file_id":"{{fileId}}"}"""
            : $$$"""{"type":"{{{type}}}","tool_use_id":"srv_1","content":{"type":"bash_code_execution_result","stdout":"ok","stderr":"","return_code":0,"content":[{"type":"bash_code_execution_output","file_id":"file_one"},{"type":"code_execution_output","file_id":"file_two"}]}}""";
        return "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"model\":\"test\",\"role\":\"assistant\",\"content\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"before\"}}\n\n"
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":3,\"content_block\":{\"type\":\"server_tool_use\",\"id\":\"srv_1\",\"name\":\"bash_code_execution\",\"input\":{}}}\n\n"
            + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":3}\n\n"
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":" + block + "}\n\n"
            + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"text_delta\",\"text\":\"after\"}}\n\n"
            + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
    }

    private static string Metadata(string id, int length, bool downloadable = true) =>
        $$"""{"id":"{{id}}","type":"file","filename":"../../report.txt","mime_type":"text/plain","size_bytes":{{length}},"downloadable":{{downloadable.ToString().ToLowerInvariant()}}}""";

    private static void Cleanup(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected test path.");
        }
        Directory.Delete(full, recursive: true);
    }
}
