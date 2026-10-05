using System.Net;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.DeepSeek;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

/// <summary>
/// Test that DeepSeek provider correctly passes stopSequences to the underlying API.
/// </summary>
[TestClass]
public sealed class DeepSeekStopSequencesTest
{
    private readonly TestContext _testContext;

    public DeepSeekStopSequencesTest(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task StopSequences_PassedToApiRequest()
    {
        // Arrange: create a fake server that captures the request
        await using var server = await FakeProviderServer.StartAsync(
            request =>
            {
                // Parse the incoming request to verify stopSequences
                var body = JsonDocument.Parse(request.Body);
                var stopArray = body.RootElement.GetProperty("stop");

                Assert.AreEqual(JsonValueKind.Array, stopArray.ValueKind);
                var stops = stopArray.EnumerateArray().Select(e => e.GetString()).ToArray();
                Assert.HasCount(1, stops);
                Assert.AreEqual("END", stops[0]);

                // Return a simple response
                return ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible);
            },
            _testContext.CancellationToken);

        using var adapter = new DeepSeekProviderAdapter(
            baseUrl: server.BaseAddress.AbsoluteUri.TrimEnd('/'),
            apiKey: "test-key",
            loggerFactory: null,
            models: null,
            providerId: "deepseek");

        var request = new RuntimeProviderRequest(
            "inv-1",
            "agent-1",
            "deepseek",
            "deepseek-v4-pro",
            [new RuntimeProviderMessage(
                RuntimeProviderRoles.User,
                [new TextContentBlock("Count to 100")])],
            Tools: null,
            Temperature: 0.5f,
            MaxTokens: 100,
            StructuredOutput: null,
            CancellationToken: _testContext.CancellationToken,
            RequestControl: new ExpertRequestControl
            {
                StopSequences = ["END"]
            });

        // Act: stream the response
        var events = new List<RuntimeProviderEvent>();
        await foreach (var evt in adapter.CompleteStreamingAsync(request, _testContext.CancellationToken))
        {
            events.Add(evt);
        }

        // Assert: verify completion and that request was made
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        var capturedRequest = Assert.ContainsSingle(server.Requests);
        StringAssert.Contains(capturedRequest.Body, "\"stop\"", "Request should contain stop field");
        StringAssert.Contains(capturedRequest.Body, "END", "Request should contain END stop sequence");
    }
}
