using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Aliyun;
using Madorin.AI.Runtime.Providers.DeepSeek;
using Madorin.AI.Runtime.Providers.Kimi;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderMediaAttachmentTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("OpenAI", false)]
    [DataRow("OpenAI", true)]
    [DataRow("Anthropic", false)]
    [DataRow("Anthropic", true)]
    [DataRow("OpenAICompatible", false)]
    [DataRow("OpenAICompatible", true)]
    [DataRow("Kimi", false)]
    [DataRow("DeepSeek", false)]
    public async Task Pdf_UsesNativeDocumentPartAndUnchangedBytes(string provider, bool toolResult)
    {
        var bytes = RandomNumberGenerator.GetBytes(1203);
        var blob = Reference(bytes, "application/pdf");
        var kind = Kind(provider);
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind), TestContext.CancellationToken);
        using var fixture = CreateAdapter(provider, server.BaseAddress, new FixtureResolver(bytes, "application/pdf"));
        var request = Request(blob, toolResult);
        var events = await ProviderAdapterTestFactory.CollectAsync(fixture.Adapter, request, TestContext.CancellationToken);
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>(),
            string.Join(" | ", events.OfType<InvocationFailedProviderEvent>().Select(e => e.Error.Message)));
        var captured = Assert.ContainsSingle(server.Requests);
        using var body = JsonDocument.Parse(captured.Body);
        var part = FindPart(body.RootElement, "input_file", "file", "document");
        var data = provider == "Anthropic" ? part.GetProperty("source").GetProperty("data").GetString()!
            : provider == "OpenAI" ? part.GetProperty("file_data").GetString()!
            : part.GetProperty("file").GetProperty("file_data").GetString()!;
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]));
        Assert.DoesNotContain(blob.BlobId, captured.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("OpenAICompatible", "audio/wav", "wav", false)]
    [DataRow("OpenAICompatible", "audio/mpeg", "mp3", true)]
    [DataRow("Kimi", "audio/wav", "wav", false)]
    [DataRow("DeepSeek", "audio/mpeg", "mp3", false)]
    public async Task ChatAudio_UsesInputAudioPart(string provider, string mime, string format, bool toolResult)
    {
        var bytes = RandomNumberGenerator.GetBytes(1037);
        var blob = Reference(bytes, mime);
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible), TestContext.CancellationToken);
        using var fixture = CreateAdapter(provider, server.BaseAddress, new FixtureResolver(bytes, mime));
        var events = await ProviderAdapterTestFactory.CollectAsync(fixture.Adapter, Request(blob, toolResult),
            TestContext.CancellationToken);
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        using var body = JsonDocument.Parse(Assert.ContainsSingle(server.Requests).Body);
        var audio = FindPart(body.RootElement, "input_audio").GetProperty("input_audio");
        Assert.AreEqual(format, audio.GetProperty("format").GetString());
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(audio.GetProperty("data").GetString()!));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AliyunVideo_UsesVideoUrlPartNotImageOrToolText(bool toolResult)
    {
        var bytes = RandomNumberGenerator.GetBytes(255_117);
        var blob = Reference(bytes, "video/mp4");
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible), TestContext.CancellationToken);
        var endpoint = new Uri(server.BaseAddress, "v1").AbsoluteUri;
        using var adapter = new AliyunProviderAdapter(new AliyunProviderOptions("aliyun", "test", endpoint,
            Models: [new(ProviderAdapterTestFactory.ModelId, "test", Capabilities: new(Streaming: true))]),
            blobResolver: new FixtureResolver(bytes, "video/mp4"));
        var events = await ProviderAdapterTestFactory.CollectAsync(adapter, Request(blob, toolResult),
            TestContext.CancellationToken);
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>(),
            string.Join(" | ", events.OfType<InvocationFailedProviderEvent>().Select(e => e.Error.Message)));
        var captured = Assert.ContainsSingle(server.Requests);
        using var body = JsonDocument.Parse(captured.Body);
        var part = FindPart(body.RootElement, "video_url");
        var uri = part.GetProperty("video_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:video/mp4;base64,", uri, StringComparison.Ordinal);
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]));
        Assert.DoesNotContain(blob.BlobId, captured.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("OpenAI", "video/mp4")]
    [DataRow("Anthropic", "video/mp4")]
    [DataRow("OpenAICompatible", "video/mp4")]
    [DataRow("OpenAI", "audio/wav")]
    [DataRow("OpenAICompatible", "application/octet-stream")]
    public async Task MissingWireCodec_FailsBeforeHttp(string provider, string mime)
    {
        byte[] bytes = [1, 2, 3, 4];
        var blob = Reference(bytes, mime);
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(Kind(provider)), TestContext.CancellationToken);
        using var fixture = CreateAdapter(provider, server.BaseAddress, new FixtureResolver(bytes, mime));
        var events = await ProviderAdapterTestFactory.CollectAsync(fixture.Adapter, Request(blob, true),
            TestContext.CancellationToken);
        var failure = Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
        Assert.Contains("codec", failure.Error.Message, StringComparison.Ordinal);
        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    public async Task AliyunOversizedInlineVideo_FailsBeforeOpeningBlobOrSendingHttp()
    {
        var blob = new BlobReference("too-large", 8_000_000, new string('0', 64), "video/mp4",
            "run:test", DateTimeOffset.UtcNow.AddMinutes(5));
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible), TestContext.CancellationToken);
        var resolver = new FixtureResolver([1, 2, 3], "video/mp4");
        using var adapter = new AliyunProviderAdapter(new AliyunProviderOptions("aliyun", "test",
            new Uri(server.BaseAddress, "v1").AbsoluteUri,
            Models: [new(ProviderAdapterTestFactory.ModelId, "test", Capabilities: new(Streaming: true))]),
            blobResolver: resolver);
        var events = await ProviderAdapterTestFactory.CollectAsync(adapter, Request(blob, false),
            TestContext.CancellationToken);
        var failure = Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
        Assert.Contains("10 MB", failure.Error.Message, StringComparison.Ordinal);
        Assert.AreEqual(0, resolver.Calls);
        Assert.IsEmpty(server.Requests);
    }

    private static BlobReference Reference(byte[] bytes, string mime) => new(
        Guid.NewGuid().ToString("N"), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), mime,
        "run:test", DateTimeOffset.UtcNow.AddMinutes(5));

    private static RuntimeProviderRequest Request(BlobReference blob, bool toolResult)
    {
        using var args = JsonDocument.Parse("{}");
        return ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = toolResult
                ? [new("user", [new TextContentBlock("inspect attachment")]),
                    new("assistant", [new ToolCallContentBlock("c", "attach", "attach", args.RootElement.Clone())]),
                    new("tool", [new ToolResultContentBlock("c", "attach", true,
                        [new TextContentBlock("attachment ready"), new BlobRefContentBlock(blob)])])]
                : [new("user", [new TextContentBlock("inspect attachment"), new BlobRefContentBlock(blob)])],
            HistoryMessageCount = toolResult ? 3 : 1
        };
    }

    private static ProviderAdapterKind Kind(string provider) => provider switch
    {
        "OpenAI" => ProviderAdapterKind.OpenAI,
        "Anthropic" => ProviderAdapterKind.Anthropic,
        _ => ProviderAdapterKind.OpenAICompatible
    };

    private static ProviderAdapterFixture CreateAdapter(string provider, Uri address, IProviderBlobResolver resolver)
    {
        ProviderModel[] models = [new(ProviderAdapterTestFactory.ModelId, "test",
            Capabilities: new(Streaming: true, Vision: false, Files: false))];
        var endpoint = new Uri(address, "v1").AbsoluteUri;
        return provider switch
        {
            "Kimi" => new(new KimiProviderAdapter("test", endpoint, models: models, blobResolver: resolver)),
            "DeepSeek" => new(new DeepSeekProviderAdapter(endpoint, "test", models: models, blobResolver: resolver)),
            _ => ProviderAdapterTestFactory.Create(Kind(provider), address,
                ProviderAdapterTestFactory.CreateCapabilities(vision: false, files: false), resolver)
        };
    }

    private static JsonElement FindPart(JsonElement root, params string[] types)
    {
        using var parsed = JsonDocument.Parse(root.GetRawText());
        var stack = new Stack<JsonElement>();
        stack.Push(parsed.RootElement);
        while (stack.TryPop(out var node))
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("type", out var type) && types.Contains(type.GetString()))
                {
                    return node.Clone();
                }
                foreach (var property in node.EnumerateObject())
                {
                    stack.Push(property.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in node.EnumerateArray())
                {
                    stack.Push(child);
                }
            }
        }

        throw new AssertFailedException("The native attachment part was not found.");
    }

    private sealed class FixtureResolver(byte[] bytes, string mime) : IProviderBlobResolver
    {
        public int Calls { get; private set; }

        public ValueTask<ResolvedProviderBlob> ResolveAsync(BlobReference blob, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(new ResolvedProviderBlob(bytes, mime,
                mime == "application/pdf" ? "fixture.pdf" : "fixture.mp4"));
        }
    }
}
