using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ExpertRequestControlTests(TestContext testContext)
{
    [TestMethod]
    public async Task WorkerInvoke_RoundTrip_PreservesRequestControl()
    {
        var control = CreateControl() with
        {
            Stream = false,
            ResponseFormat = "json",
            Metadata = new() { ["trace"] = "request-1" }
        };
        var request = ProviderAdapterTestFactory.CreateRequest() with { RequestControl = control };
        await using var stream = new MemoryStream();
        await ProviderWorkerFrameCodec.WriteAsync(stream, new InvokeFrame(
            request.InvocationId,
            new ProviderConnectionConfig("test", "OpenAI Compatible", "http://localhost", "test-key"),
            request), testContext.CancellationToken);
        stream.Position = 0;

        var read = await ProviderWorkerFrameCodec.ReadAsync(stream, testContext.CancellationToken);
        var restored = read.Frame as InvokeFrame;
        Assert.IsNotNull(restored);
        Assert.IsNotNull(restored.Request.RequestControl);
        Assert.IsTrue(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(control, ProviderWorkerJsonContext.Default.ExpertRequestControl),
            JsonSerializer.SerializeToElement(restored.Request.RequestControl, ProviderWorkerJsonContext.Default.ExpertRequestControl)));
    }

    [TestMethod]
    public void ProviderRequest_WithoutControl_RemainsCompatible()
    {
        var json = JsonSerializer.Serialize(
            ProviderAdapterTestFactory.CreateRequest(), RuntimeProviderJsonContext.Default.RuntimeProviderRequest);
        using var document = JsonDocument.Parse(json);
        Assert.IsFalse(document.RootElement.TryGetProperty("requestControl", out _));
        var restored = JsonSerializer.Deserialize(json, RuntimeProviderJsonContext.Default.RuntimeProviderRequest);
        Assert.IsNotNull(restored);
        Assert.IsNull(restored.RequestControl);
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task SamplingOverrides_AreSentToProvider(ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind), testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);
        var control = CreateControl();
        var request = ProviderAdapterTestFactory.CreateRequest() with { RequestControl = control };

        var events = await ProviderAdapterTestFactory.CollectAsync(fixture.Adapter, request, testContext.CancellationToken);

        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        using var body = JsonDocument.Parse(Assert.ContainsSingle(server.Requests).Body);
        var root = body.RootElement;
        Assert.AreEqual(control.Temperature!.Value, root.GetProperty("temperature").GetDouble(), 0.000001);
        Assert.AreEqual(control.TopP!.Value, root.GetProperty("top_p").GetDouble(), 0.000001);
        var tokenField = kind switch
        {
            ProviderAdapterKind.OpenAI => "max_output_tokens",
            ProviderAdapterKind.Anthropic => "max_tokens",
            _ => "max_completion_tokens"
        };
        Assert.AreEqual(control.MaxTokens, root.GetProperty(tokenField).GetInt32());
        if (kind != ProviderAdapterKind.OpenAI)
        {
            var stops = root.GetProperty(kind == ProviderAdapterKind.Anthropic ? "stop_sequences" : "stop");
            CollectionAssert.AreEqual(control.StopSequences, stops.EnumerateArray().Select(value => value.GetString()).ToArray());
        }
        if (kind == ProviderAdapterKind.OpenAICompatible)
        {
            Assert.AreEqual(control.FrequencyPenalty!.Value, root.GetProperty("frequency_penalty").GetDouble());
            Assert.AreEqual(control.PresencePenalty!.Value, root.GetProperty("presence_penalty").GetDouble());
        }
        if (kind == ProviderAdapterKind.Anthropic)
        {
            Assert.AreEqual(control.TopK, root.GetProperty("top_k").GetInt32());
        }
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Validate_NonFiniteSamplingValues_RejectsBeforeSerialization(double value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ExpertRequestControl { Temperature = value }.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new ExpertRequestControl { TopP = value }.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new ExpertRequestControl { FrequencyPenalty = value }.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new ExpertRequestControl { PresencePenalty = value }.Validate());
    }

    private static ExpertRequestControl CreateControl() => new()
    {
        Temperature = 0.75,
        MaxTokens = 321,
        TopP = 0.00001,
        TopK = 13,
        StopSequences = ["END", "STOP"],
        FrequencyPenalty = 0.5,
        PresencePenalty = -0.25
    };
}
