using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Server.ProviderWorkers;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderAttachmentDeliveryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("direct")]
    [DataRow("worker")]
    [DataRow("anthropic")]
    [DataRow("missing")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task LocalRuntime_PersistsAttachmentInStartupDirectoryAndPushesMarkdownInOrder(string route)
    {
        var root = Path.Combine(Path.GetTempPath(), "attachment-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? deliveredPath = null;
        try
        {
            var bytes = route == "anthropic"
                ? System.Text.Encoding.UTF8.GetBytes(new string('a', 300_001))
                : RandomNumberGenerator.GetBytes(300_001);
            var image = "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":0,\"item_id\":\"msg-0\",\"output_index\":0,\"content_index\":0,\"delta\":\"before\",\"logprobs\":[]}\n\n"
                + "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"sequence_number\":1,\"output_index\":1,\"item\":{\"id\":\"ig\",\"type\":\"image_generation_call\",\"status\":\"completed\",\"result\":\""
                + Convert.ToBase64String(bytes) + "\"}}\n\n";
            await using var server = await FakeProviderServer.StartAsync(r => route == "anthropic"
                ? r.Method == "POST"
                    ? FakeProviderResponse.EventStream(new FakeProviderChunk(AnthropicFileOutputTests.CreateStream("container_upload")))
                    : r.Path.EndsWith("/content", StringComparison.Ordinal)
                        ? new(200, "text/plain", [new(System.Text.Encoding.UTF8.GetString(bytes))])
                        : FakeProviderResponse.Error(200, "{\"id\":\"file_one\",\"filename\":\"result.txt\",\"mime_type\":\"text/plain\",\"size_bytes\":300001,\"downloadable\":true}")
                : FakeProviderResponse.EventStream(
                    [new FakeProviderChunk(image), .. ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAI).Chunks]),
                TestContext.CancellationToken);
            await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
            var protocol = route == "anthropic" ? "Anthropic" : "OpenAI";
            IRuntimeProviderAdapter provider = route is "worker" or "anthropic"
                ? supervisor.CreateAdapter(protocol, new ProviderConnectionConfig("p", protocol,
                    new Uri(server.BaseAddress, "v1").AbsoluteUri, "test", Models: ["test"]))
                : new OutputProvider(bytes, route == "missing");
            var data = Path.Join(root, "data");
            await using var runtime = await LocalRuntime.StartAsync(new LocalRuntimeOptions(root, data,
                Path.Join(root, "logs"), Guid.NewGuid().ToString("N"), _ => provider)
                { MemoryUserHome = Path.Join(root, "home") }, TestContext.CancellationToken);
            var selection = new NextTurnSelection(1, RuntimeMode.Expert, new("p", "test"),
                new ExpertModeOptions(new AgentRef("a", "1", "test", "p", "test")));
            var request = new NewSessionRunRequest("r", "t", RuntimeMode.Expert, selection,
                [new TextContentBlock("return an attachment")]);
            var events = new List<RuntimeEventEnvelope>();
            await foreach (var item in runtime.RunAsync(request, TestContext.CancellationToken))
            {
                events.Add(item);
            }
            var text = string.Concat(events.Where(e => e.MessageType == MessageTypes.TextDelta)
                .Select(e => e.Payload.Deserialize(RuntimeJsonContext.Default.TextDeltaEvent)!.Delta));
            if (route == "missing")
            {
                var failed = Assert.ContainsSingle(events.Where(e => e.MessageType == MessageTypes.RunFailed));
                Assert.Contains("AttachmentDeliveryFailed", failed.Payload.GetRawText(), StringComparison.Ordinal);
                Assert.IsEmpty(events.Where(e => e.MessageType == MessageTypes.RunCompleted));
                Assert.AreEqual("before", text);
                return;
            }
            Assert.IsFalse(events.Any(e => e.MessageType == MessageTypes.RunFailed),
                string.Join(" | ", events.Where(e => e.MessageType == MessageTypes.RunFailed).Select(e => e.Payload.GetRawText())));
            var completed = Assert.ContainsSingle(events.Where(e => e.MessageType == MessageTypes.RunCompleted));
            var begin = text.IndexOf("(file:", StringComparison.Ordinal);
            Assert.IsGreaterThan(0, begin);
            var end = text.IndexOf(')', begin);
            deliveredPath = new Uri(text[(begin + 1)..end]).LocalPath;
            Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, ".attach"), Path.GetDirectoryName(deliveredPath));
            Assert.AreEqual(route == "anthropic" ? ".txt" : ".png", Path.GetExtension(deliveredPath));
            Assert.IsTrue(Guid.TryParseExact(Path.GetFileNameWithoutExtension(deliveredPath), "N", out _));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(deliveredPath, TestContext.CancellationToken));
            Assert.IsTrue(text.StartsWith(route == "anthropic" ? "before\n\n[" : "before\n\n![", StringComparison.Ordinal));
            Assert.EndsWith(route == "worker" ? "Hello" : "after", text, StringComparison.Ordinal);
            var sessionId = completed.Payload.GetProperty("sessionId").GetString()!;
            var history = await runtime.ReadSessionHistoryAsync(sessionId, TestContext.CancellationToken);
            var assistant = history.Where(r => r.Role == "assistant").Last();
            var blocks = assistant.Content.Deserialize(RuntimeJsonContext.Default.ContentBlockArray)!;
            Assert.ContainsSingle(blocks.OfType<BlobRefContentBlock>(), assistant.Content.GetRawText());
            Assert.IsTrue(blocks.OfType<TextContentBlock>().Any(t => t.Text.Contains("file:///", StringComparison.Ordinal)));
        }
        finally
        {
            if (deliveredPath is not null && Path.GetDirectoryName(deliveredPath) == Path.Combine(AppContext.BaseDirectory, ".attach"))
            {
                File.Delete(deliveredPath);
            }
            SqliteConnection.ClearAllPools();
            Cleanup(root);
        }
    }

    private static void Cleanup(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected test path.");
        }
        Directory.Delete(full, recursive: true);
    }

    private sealed class OutputProvider(byte[] bytes, bool missing) : IRuntimeProviderAdapter
    {
        public string ProviderId => "p";
        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);
        public IReadOnlyList<ProviderModel> Models => [new("test", "test")];
        public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default) => Task.CompletedTask;
        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TextDeltaProviderEvent(request.InvocationId, "before");
            using var source = new MemoryStream(bytes, writable: false);
            var blob = missing
                ? new BlobReference(Guid.NewGuid().ToString("N"), bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes)), "image/png", "run:missing", DateTimeOffset.UtcNow.AddHours(1))
                : await request.StoreOutputBlob!(source, "image/png", bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes)), ct);
            yield return new AttachmentCompletedProviderEvent(request.InvocationId, blob, "../../unsafe-name.png");
            yield return new TextDeltaProviderEvent(request.InvocationId, "after");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }
    }
}
