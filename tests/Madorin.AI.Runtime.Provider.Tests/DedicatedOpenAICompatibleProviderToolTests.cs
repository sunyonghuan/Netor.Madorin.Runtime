using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.DeepSeek;
using Madorin.AI.Runtime.Providers.Kimi;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class DedicatedOpenAICompatibleProviderToolTests
{
    private const string ProviderToolName = "builtin__fs__list";
    private const string RuntimeToolId = "builtin.fs.list";

    public TestContext TestContext { get; set; }

    [TestMethod]
    [DataRow(DedicatedProviderKind.DeepSeek)]
    [DataRow(DedicatedProviderKind.Kimi)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_ToolsAreDeclaredAndToolCallsAreMapped(
        DedicatedProviderKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => CreateToolCallResponse(),
            TestContext.CancellationToken);
        using var fixture = CreateAdapter(kind, server.BaseAddress);
        var tools = CreateTools();

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest(tools),
            TestContext.CancellationToken);

        var delta = Assert.ContainsSingle(events.OfType<ToolCallDeltaProviderEvent>());
        Assert.AreEqual("call-1", delta.CallId);
        Assert.AreEqual(ProviderToolName, delta.Name);
        Assert.AreEqual("{\"city\":\"Shanghai\"}", delta.ArgumentsDelta);

        var completed = Assert.ContainsSingle(events.OfType<ToolCallCompleteProviderEvent>());
        Assert.AreEqual("call-1", completed.CallId);
        Assert.AreEqual(RuntimeToolId, completed.ToolId);
        Assert.AreEqual(ProviderToolName, completed.Name);
        Assert.AreEqual("{\"city\":\"Shanghai\"}", completed.ArgumentsJson);
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());

        var captured = Assert.ContainsSingle(server.Requests);
        using var body = JsonDocument.Parse(captured.Body);
        var declaration = body.RootElement.GetProperty("tools")[0].GetProperty("function");
        Assert.AreEqual(ProviderToolName, declaration.GetProperty("name").GetString());
        Assert.AreEqual(
            "Gets the current weather.",
            declaration.GetProperty("description").GetString());
        var parameters = declaration.GetProperty("parameters");
        Assert.AreEqual("object", parameters.GetProperty("type").GetString());
        Assert.AreEqual(
            "string",
            parameters.GetProperty("properties").GetProperty("city").GetProperty("type").GetString());
        Assert.AreEqual("city", parameters.GetProperty("required")[0].GetString());
    }

    [TestMethod]
    [DataRow(DedicatedProviderKind.DeepSeek)]
    [DataRow(DedicatedProviderKind.Kimi)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_ToolHistory_ForwardsCallAndResult(
        DedicatedProviderKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            TestContext.CancellationToken);
        using var fixture = CreateAdapter(kind, server.BaseAddress);
        using var arguments = JsonDocument.Parse("{\"city\":\"history-city-marker\"}");
        RuntimeProviderMessage[] messages =
        [
            new(RuntimeProviderRoles.User, [new TextContentBlock("weather question")]),
            new(
                RuntimeProviderRoles.Assistant,
                [
                    new ToolCallContentBlock(
                        "history-call-marker",
                        RuntimeToolId,
                        "List files",
                        arguments.RootElement.Clone())
                ]),
            new(
                RuntimeProviderRoles.Tool,
                [
                    new ToolResultContentBlock(
                        "history-call-marker",
                        RuntimeToolId,
                        true,
                        [new TextContentBlock("history-result-marker")])
                ])
        ];
        var request = ProviderAdapterTestFactory.CreateRequest(CreateTools()) with
        {
            Messages = messages
        };

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            request,
            TestContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        var captured = Assert.ContainsSingle(server.Requests);
        using var body = JsonDocument.Parse(captured.Body);
        var requestMessages = body.RootElement.GetProperty("messages");
        var assistant = requestMessages.EnumerateArray().Single(
            static message => message.GetProperty("role").GetString() == "assistant");
        var toolCall = assistant.GetProperty("tool_calls")[0];
        Assert.AreEqual("history-call-marker", toolCall.GetProperty("id").GetString());
        Assert.AreEqual(
            ProviderToolName,
            toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Contains(
            "history-city-marker",
            toolCall.GetProperty("function").GetProperty("arguments").GetString()!,
            StringComparison.Ordinal);

        var toolResult = requestMessages.EnumerateArray().Single(
            static message => message.GetProperty("role").GetString() == "tool");
        Assert.AreEqual("history-call-marker", toolResult.GetProperty("tool_call_id").GetString());
        Assert.AreEqual("history-result-marker", toolResult.GetProperty("content").GetString());
    }

    [TestMethod]
    [DataRow(DedicatedProviderKind.DeepSeek)]
    [DataRow(DedicatedProviderKind.Kimi)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_InvalidDisplayName_UsesProviderSafeToolName(
        DedicatedProviderKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            TestContext.CancellationToken);
        using var fixture = CreateAdapter(kind, server.BaseAddress);
        ToolDescriptor[] tools =
        [
            new(
                "builtin.fs.read",
                "builtin",
                "Read file",
                "Reads one file.",
                "{\"type\":\"object\"}")
        ];

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest(tools),
            TestContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        var captured = Assert.ContainsSingle(server.Requests);
        using var body = JsonDocument.Parse(captured.Body);
        var declaration = body.RootElement.GetProperty("tools")[0].GetProperty("function");
        Assert.AreEqual("builtin__fs__read", declaration.GetProperty("name").GetString());
    }

    private static ToolDescriptor[] CreateTools() =>
    [
        new(
            RuntimeToolId,
            "builtin",
            "List files",
            "Gets the current weather.",
            "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"]}")
    ];

    private static FakeProviderResponse CreateToolCallResponse()
    {
        var response = ProviderProtocolScripts.ToolCall(ProviderAdapterKind.OpenAICompatible);
        return response with
        {
            Chunks =
            [
                .. response.Chunks.Select(chunk => chunk with
                {
                    Content = chunk.Content.Replace(
                        "get_weather",
                        ProviderToolName,
                        StringComparison.Ordinal)
                })
            ]
        };
    }

    private static ProviderAdapterFixture CreateAdapter(DedicatedProviderKind kind, Uri baseAddress)
    {
        ProviderModel[] models =
        [
            new(
                ProviderAdapterTestFactory.ModelId,
                "Test model",
                Capabilities: ProviderAdapterTestFactory.CreateCapabilities())
        ];
        var endpoint = $"{baseAddress.AbsoluteUri.TrimEnd('/')}/v1";
        IRuntimeProviderAdapter adapter = kind switch
        {
            DedicatedProviderKind.DeepSeek => new DeepSeekProviderAdapter(
                endpoint,
                ProviderAdapterTestFactory.ApiKey,
                models: models),
            DedicatedProviderKind.Kimi => new KimiProviderAdapter(
                ProviderAdapterTestFactory.ApiKey,
                endpoint,
                models: models),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        return new ProviderAdapterFixture(adapter);
    }
}

public enum DedicatedProviderKind
{
    DeepSeek,
    Kimi
}
