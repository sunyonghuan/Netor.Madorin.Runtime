using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.DeepSeek;
using Madorin.AI.Runtime.Providers.Kimi;
using Microsoft.Extensions.AI;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ToolAttachmentRequestTests
{
    private static readonly string[] MediaTypes = ["image/png", "audio/wav", "application/pdf"];
    public TestContext TestContext { get; set; }

    [TestMethod]
    [DataRow("OpenAI")]
    [DataRow("Anthropic")]
    [DataRow("OpenAICompatible")]
    [DataRow("Kimi")]
    [DataRow("DeepSeek")]
    public async Task ToolImages_ReachNativePartsWithCallIdsAndUnchangedHistory(string provider)
    {
        var kind = provider == "OpenAI" ? ProviderAdapterKind.OpenAI
            : provider == "Anthropic" ? ProviderAdapterKind.Anthropic : ProviderAdapterKind.OpenAICompatible;
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jP1kAAAAASUVORK5CYII=");
        var blob = new BlobReference("private-blob-id", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), "image/png", "run:private-run",
            DateTimeOffset.UtcNow.AddMinutes(5));
        var resolver = new Resolver(bytes);
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind), TestContext.CancellationToken);
        using var fixture = CreateAdapter(provider, kind, server.BaseAddress, resolver);
        using var arguments = JsonDocument.Parse("{}");
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages =
            [
                new(RuntimeProviderRoles.User, [new TextContentBlock("inspect both images")]),
                new(RuntimeProviderRoles.Assistant,
                [
                    new ToolCallContentBlock("call-1", "attach", "attach", arguments.RootElement.Clone()),
                    new ToolCallContentBlock("call-2", "attach", "attach", arguments.RootElement.Clone())
                ]),
                new(RuntimeProviderRoles.Tool,
                    [new ToolResultContentBlock("call-1", "attach", true,
                        [new TextContentBlock("first metadata"), new BlobRefContentBlock(blob)])]),
                new(RuntimeProviderRoles.Tool,
                    [new ToolResultContentBlock("call-2", "attach", true,
                        [new TextContentBlock("second metadata"), new BlobRefContentBlock(blob)])])
            ],
            HistoryMessageCount = 4
        };
        var before = JsonSerializer.Serialize(request, RuntimeProviderJsonContext.Default.RuntimeProviderRequest);

        var events = await ProviderAdapterTestFactory.CollectAsync(fixture.Adapter,
            request, TestContext.CancellationToken);

        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        var captured = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual(provider == "OpenAI" ? "/v1/responses"
            : provider == "Anthropic" ? "/v1/messages" : "/v1/chat/completions", captured.Path);
        using var body = JsonDocument.Parse(captured.Body);
        var root = body.RootElement;
        if (provider == "OpenAI")
        {
            var outputs = root.GetProperty("input").EnumerateArray()
                .Where(static part => part.GetProperty("type").GetString() == "function_call_output").ToArray();
            Assert.HasCount(2, outputs);
            for (var i = 0; i < outputs.Length; i++)
            {
                Assert.AreEqual($"call-{i + 1}", outputs[i].GetProperty("call_id").GetString());
                var parts = outputs[i].GetProperty("output");
                Assert.AreEqual("input_text", parts[0].GetProperty("type").GetString());
                Assert.AreEqual("input_image", parts[1].GetProperty("type").GetString());
                AssertImage(parts[1].GetProperty("image_url").GetString()!, bytes);
            }
        }
        else if (provider == "Anthropic")
        {
            var outputs = root.GetProperty("messages").EnumerateArray()
                .SelectMany(static message => message.GetProperty("content").EnumerateArray())
                .Where(static part => part.GetProperty("type").GetString() == "tool_result").ToArray();
            Assert.HasCount(2, outputs);
            for (var i = 0; i < outputs.Length; i++)
            {
                Assert.AreEqual($"call-{i + 1}", outputs[i].GetProperty("tool_use_id").GetString());
                var source = outputs[i].GetProperty("content")[1].GetProperty("source");
                Assert.AreEqual("base64", source.GetProperty("type").GetString());
                Assert.AreEqual("image/png", source.GetProperty("media_type").GetString());
                CollectionAssert.AreEqual(bytes, Convert.FromBase64String(source.GetProperty("data").GetString()!));
            }
        }
        else
        {
            var messages = root.GetProperty("messages").EnumerateArray().ToArray();
            var results = messages.Where(static message => message.GetProperty("role").GetString() == "tool").ToArray();
            Assert.HasCount(2, results);
            for (var i = 0; i < results.Length; i++)
            {
                Assert.AreEqual($"call-{i + 1}", results[i].GetProperty("tool_call_id").GetString());
                Assert.DoesNotContain("base64", results[i].GetProperty("content").GetString()!, StringComparison.Ordinal);
            }

            var parts = messages[^1].GetProperty("content");
            Assert.AreEqual("user", messages[^1].GetProperty("role").GetString());
            AssertImage(parts[1].GetProperty("image_url").GetProperty("url").GetString()!, bytes);
            AssertImage(parts[3].GetProperty("image_url").GetProperty("url").GetString()!, bytes);
        }

        Assert.DoesNotContain(blob.BlobId, captured.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(blob.AccessScope, captured.Body, StringComparison.Ordinal);
        Assert.AreEqual(before, JsonSerializer.Serialize(request, RuntimeProviderJsonContext.Default.RuntimeProviderRequest));
        Assert.AreEqual(2, resolver.Calls);
    }

    private static ProviderAdapterFixture CreateAdapter(string provider, ProviderAdapterKind kind,
        Uri address, IProviderBlobResolver resolver)
    {
        var capabilities = ProviderAdapterTestFactory.CreateCapabilities(vision: false, files: false);
        ProviderModel[] models = [new(ProviderAdapterTestFactory.ModelId, "test", Capabilities: capabilities)];
        var endpoint = new Uri(address, "v1").AbsoluteUri;
        return provider switch
        {
            "Kimi" => new(new KimiProviderAdapter(ProviderAdapterTestFactory.ApiKey, endpoint,
                models: models, blobResolver: resolver)),
            "DeepSeek" => new(new DeepSeekProviderAdapter(endpoint, ProviderAdapterTestFactory.ApiKey,
                models: models, blobResolver: resolver)),
            _ => ProviderAdapterTestFactory.Create(kind, address, capabilities, resolver)
        };
    }

    [TestMethod]
    [DataRow("OpenAI")]
    [DataRow("Anthropic")]
    [DataRow("OpenAICompatible")]
    [DataRow("Kimi")]
    [DataRow("DeepSeek")]
    public async Task InvalidToolBlob_IsReportedBeforeSendingHttp(string provider)
    {
        var kind = provider == "OpenAI" ? ProviderAdapterKind.OpenAI
            : provider == "Anthropic" ? ProviderAdapterKind.Anthropic : ProviderAdapterKind.OpenAICompatible;
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind), TestContext.CancellationToken);
        using var fixture = CreateAdapter(provider, kind, server.BaseAddress, new Resolver([1, 2, 3]));
        var blob = new BlobReference("corrupt", 100, new string('0', 64), "image/png",
            "run:test", DateTimeOffset.UtcNow.AddMinutes(5));
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new(RuntimeProviderRoles.Tool,
                [new ToolResultContentBlock("call", "attach", true, [new BlobRefContentBlock(blob)])])]
        };

        var events = await ProviderAdapterTestFactory.CollectAsync(fixture.Adapter, request,
            TestContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    [DataRow("OpenAI")]
    [DataRow("Anthropic")]
    [DataRow("OpenAICompatible")]
    [DataRow("Kimi")]
    [DataRow("DeepSeek")]
    public async Task UnsupportedToolMedia_ReportsMissingCodecWithoutSendingReference(string provider)
    {
        var kind = provider == "OpenAI" ? ProviderAdapterKind.OpenAI
            : provider == "Anthropic" ? ProviderAdapterKind.Anthropic : ProviderAdapterKind.OpenAICompatible;
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind), TestContext.CancellationToken);
        using var fixture = CreateAdapter(provider, kind, server.BaseAddress, new Resolver([1, 2, 3]));
        var blob = new BlobReference("video", 3, new string('0', 64), "video/mp4",
            "run:test", DateTimeOffset.UtcNow.AddMinutes(5));
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new(RuntimeProviderRoles.Tool,
                [new ToolResultContentBlock("call", "attach", true, [new BlobRefContentBlock(blob)])])]
        };
        var events = await ProviderAdapterTestFactory.CollectAsync(fixture.Adapter, request,
            TestContext.CancellationToken);
        var failure = Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
        Assert.Contains("codec", failure.Error.Message, StringComparison.Ordinal);
        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    public async Task BlobResolver_UsesByteMemoryWithoutCopyingAndRemovesPrivatePathFromName()
    {
        var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        var blob = new BlobReference("blob", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            "image/png", "run:test", DateTimeOffset.UtcNow.AddMinutes(5));
        var part = await ProviderBlobContentResolver.ResolveAsync(
            new NamedResolver(bytes), blob, TestContext.CancellationToken);
        Assert.IsTrue(part.Data.Equals((ReadOnlyMemory<byte>)bytes));
        Assert.AreEqual("sample.png", part.Name);
    }

    [TestMethod]
    [DataRow("length")]
    [DataRow("hash")]
    [DataRow("mime")]
    [DataRow("expired")]
    [DataRow("missing")]
    public async Task BlobResolver_RejectsInvalidDescriptorOrMissingResolver(string problem)
    {
        byte[] bytes = [1, 2, 3];
        var blob = new BlobReference("blob", problem == "length" ? 4 : bytes.Length,
            problem == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)),
            problem == "mime" ? "image/jpeg" : "image/png", "run:test",
            problem == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddMinutes(5));
        if (problem == "missing")
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await ProviderBlobContentResolver.ResolveAsync(null, blob, TestContext.CancellationToken));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await ProviderBlobContentResolver.ResolveAsync(new Resolver(bytes), blob, TestContext.CancellationToken));
        }
    }

    [TestMethod]
    public async Task BlobResolver_CancellationReachesPendingRead()
    {
        var resolver = new PendingResolver();
        var blob = new BlobReference("blob", 4, new string('0', 64), "image/png", "run:test",
            DateTimeOffset.UtcNow.AddMinutes(5));
        using var cts = new CancellationTokenSource();
        var task = ProviderBlobContentResolver.ResolveAsync(resolver, blob, cts.Token).AsTask();
        await resolver.Started.Task.WaitAsync(TestContext.CancellationToken);
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        Assert.IsTrue(resolver.Exited);
    }

    [TestMethod]
    [DataRow("Kimi")]
    [DataRow("DeepSeek")]
    public async Task DedicatedTopLevelImage_UsesImageUrlWithoutLocalCapabilityBlock(string provider)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var blob = new BlobReference("top-level", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            "image/png", "run:test", DateTimeOffset.UtcNow.AddMinutes(5));
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible), TestContext.CancellationToken);
        using var fixture = CreateAdapter(provider, ProviderAdapterKind.OpenAICompatible,
            server.BaseAddress, new Resolver(bytes));
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new(RuntimeProviderRoles.User, [new BlobRefContentBlock(blob)])]
        };
        var events = await ProviderAdapterTestFactory.CollectAsync(fixture.Adapter, request,
            TestContext.CancellationToken);
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        using var body = JsonDocument.Parse(Assert.ContainsSingle(server.Requests).Body);
        AssertImage(body.RootElement.GetProperty("messages")[0].GetProperty("content")[0]
            .GetProperty("image_url").GetProperty("url").GetString()!, bytes);
    }

    [TestMethod]
    [DataRow("OpenAI")]
    [DataRow("Anthropic")]
    [DataRow("OpenAICompatible")]
    [DataRow("Kimi")]
    [DataRow("DeepSeek")]
    public async Task ToolReadCancellation_StopsAdapterBeforeHttp(string provider)
    {
        var kind = provider == "OpenAI" ? ProviderAdapterKind.OpenAI
            : provider == "Anthropic" ? ProviderAdapterKind.Anthropic : ProviderAdapterKind.OpenAICompatible;
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind), TestContext.CancellationToken);
        var resolver = new PendingResolver();
        using var fixture = CreateAdapter(provider, kind, server.BaseAddress, resolver);
        using var cts = new CancellationTokenSource();
        var blob = new BlobReference("pending", 4, new string('0', 64), "image/png",
            "run:test", DateTimeOffset.UtcNow.AddMinutes(5));
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new(RuntimeProviderRoles.Tool,
                [new ToolResultContentBlock("call", "attach", true, [new BlobRefContentBlock(blob)])])]
        };
        var collect = ProviderAdapterTestFactory.CollectAsync(fixture.Adapter, request, cts.Token);
        await resolver.Started.Task.WaitAsync(TestContext.CancellationToken);
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await collect);
        Assert.IsTrue(resolver.Exited);
        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    public async Task LargeToolBlob_StaysOutsideInvokeJsonAndToolText()
    {
        var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        var blob = new BlobReference("large", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            "image/png", "run:test", DateTimeOffset.UtcNow.AddMinutes(5));
        var result = new ToolResultContentBlock("call", "attach", true,
            [new TextContentBlock("metadata"), new BlobRefContentBlock(blob)]);
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new(RuntimeProviderRoles.Tool, [result])]
        };
        var json = JsonSerializer.Serialize(request, RuntimeProviderJsonContext.Default.RuntimeProviderRequest);
        Assert.IsLessThan(2000, json.Length);
        var textOnly = request with
        {
            Messages = [new(RuntimeProviderRoles.Tool,
                [result with { Content = [new TextContentBlock("metadata")] }])]
        };
        Assert.AreEqual(ProviderTokenEstimator.Estimate(textOnly).InputTokens,
            ProviderTokenEstimator.Estimate(request).InputTokens);
        var parts = (List<AIContent>)await ProviderToolResultMapper.ToContentAsync(result,
            (reference, ct) => ProviderBlobContentResolver.ResolveAsync(new Resolver(bytes), reference, ct),
            TestContext.CancellationToken);
        Assert.AreEqual("metadata", ((TextContent)parts[0]).Text);
        Assert.AreEqual(bytes.Length, ((DataContent)parts[1]).Data.Length);
        TestContext.WriteLine($"Attachment bytes={bytes.Length}; Invoke JSON chars={json.Length}; " +
            $"estimated text tokens={ProviderTokenEstimator.Estimate(request).InputTokens}; " +
            $"protocol Base64 chars={(bytes.Length + 2L) / 3 * 4} (attachment field only).");
    }

    [TestMethod]
    [DataRow("OpenAI")]
    [DataRow("Anthropic")]
    [DataRow("OpenAICompatible")]
    [DataRow("Kimi")]
    [DataRow("DeepSeek")]
    [DoNotParallelize]
    public async Task LargeToolImage_RecordsSdkBufferingSample(string provider)
    {
        var kind = provider == "OpenAI" ? ProviderAdapterKind.OpenAI
            : provider == "Anthropic" ? ProviderAdapterKind.Anthropic : ProviderAdapterKind.OpenAICompatible;
        var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        var blob = new BlobReference("large-sample", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            "image/png", "run:test", DateTimeOffset.UtcNow.AddMinutes(5));
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind), TestContext.CancellationToken);
        using var fixture = CreateAdapter(provider, kind, server.BaseAddress, new Resolver(bytes));
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new(RuntimeProviderRoles.Tool,
                [new ToolResultContentBlock("call", "attach", true, [new BlobRefContentBlock(blob)])])]
        };
        var initialHeap = GC.GetTotalMemory(false);
        var peakHeap = initialHeap;
        var initialAllocated = GC.GetTotalAllocatedBytes();
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        // 采样包含本机 HTTP 接收端的缓冲，不作为生产 SDK 的精确峰值或稳定性能断言。
        var sampler = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1));
                while (await timer.WaitForNextTickAsync(sampling.Token).ConfigureAwait(false))
                {
                    peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
                }
            }
            catch (OperationCanceledException) when (sampling.IsCancellationRequested)
            {
            }
        }, TestContext.CancellationToken);
        List<RuntimeProviderEvent> events;
        try
        {
            events = await ProviderAdapterTestFactory.CollectAsync(fixture.Adapter, request,
                TestContext.CancellationToken);
        }
        finally
        {
            await sampling.CancelAsync();
            await sampler;
        }

        peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        var body = Assert.ContainsSingle(server.Requests).Body;
        Assert.IsGreaterThan(bytes.Length, body.Length);
        Assert.DoesNotContain(blob.BlobId, body, StringComparison.Ordinal);
        TestContext.WriteLine($"{provider}: bytes={bytes.Length}; HTTP chars={body.Length}; " +
            $"sampled heap delta={Math.Max(0, peakHeap - initialHeap)}; " +
            $"allocated delta={GC.GetTotalAllocatedBytes() - initialAllocated}; " +
            "includes fake HTTP server and test host; random binary is a buffering fixture, not a valid image.");
    }

    [TestMethod]
    public void HistoryProjection_PreservesAllMediaWhenFlagsAreFalse()
    {
        ContentBlock[] content = [.. MediaTypes.Select(mime =>
            new BlobRefContentBlock(new BlobReference(mime, 4, new string('0', 64), mime,
                "run:test", DateTimeOffset.UtcNow.AddMinutes(5))))];
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages = [new(RuntimeProviderRoles.User, content)],
            HistoryMessageCount = 1
        };
        var projection = ProviderRequestCompatibilityProjector.ProjectWithDiagnostics(request,
            ProviderAdapterTestFactory.CreateCapabilities(vision: false, files: false));
        Assert.HasCount(3, projection.Request.Messages[0].Content);
        Assert.AreSame(request, projection.Request);
    }

    private sealed class PendingResolver : IProviderBlobResolver
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Exited { get; private set; }

        public async ValueTask<ResolvedProviderBlob> ResolveAsync(BlobReference blob, CancellationToken ct = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                throw new InvalidOperationException();
            }
            finally
            {
                Exited = true;
            }
        }
    }

    private static void AssertImage(string uri, byte[] bytes)
    {
        Assert.StartsWith("data:image/png;base64,", uri, StringComparison.Ordinal);
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]));
    }

    private sealed class Resolver(byte[] bytes) : IProviderBlobResolver
    {
        public int Calls { get; private set; }
        public ValueTask<ResolvedProviderBlob> ResolveAsync(BlobReference blob, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(new ResolvedProviderBlob(bytes, "image/png", "sample.png"));
        }
    }

    private sealed class NamedResolver(byte[] bytes) : IProviderBlobResolver
    {
        public ValueTask<ResolvedProviderBlob> ResolveAsync(BlobReference blob, CancellationToken ct = default) =>
            ValueTask.FromResult(new ResolvedProviderBlob(bytes, "image/png", "C:\\private\\sample.png"));
    }
}
