using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class SummaryProjectionTests
{
    public const string ValidJson = "{\"goals\":[\"Finish task\"],\"completedFacts\":[\"File A checked\"],\"decisionsAndConstraints\":[\"Keep original data\"],\"pendingWork\":[\"Run tests\"],\"identifiers\":[\"task-42\"]}";
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Summary_IsolatesHistoricalRolesAndDisablesTools()
    {
        var provider = new SummaryProvider(ValidJson);
        var result = await GenerateAsync(provider);
        Assert.IsTrue(StrictConversationSummarizer.IsValidatedSummary(result));
        var request = provider.Requests.Single();
        Assert.HasCount(2, request.Messages);
        Assert.AreEqual(RuntimeProviderRoles.User, request.Messages[1].Role);
        Assert.IsNull(request.Tools);
        Assert.AreEqual("none", request.RequestControl?.ToolUseMode);
        StringAssert.Contains(((TextContentBlock)request.Messages[1].Content[0]).Text, "historicalRole");
        StringAssert.Contains(((TextContentBlock)result.Content[0]).Text, "task-42");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("continue with the next task")]
    [DataRow("{\"goals\":[\"goal\"]}")]
    [DataRow("{\"goals\":[\"<DSML>memory_get_original</DSML>\"],\"completedFacts\":[],\"decisionsAndConstraints\":[],\"pendingWork\":[],\"identifiers\":[]}")]
    [DataRow("{\"goals\":[\"\\u003ctool_call>memory_get_original\"],\"completedFacts\":[],\"decisionsAndConstraints\":[],\"pendingWork\":[],\"identifiers\":[]}")]
    [DataRow("{\"goals\":[],\"completedFacts\":[],\"decisionsAndConstraints\":[],\"pendingWork\":[],\"identifiers\":[]}")]
    public async Task Summary_InvalidOutputGetsOnlyOneCorrection(string invalid)
    {
        var provider = new SummaryProvider(invalid);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => GenerateAsync(provider));
        StringAssert.Contains(exception.Message, "summary_invalid");
        Assert.HasCount(2, provider.Requests);
    }

    [TestMethod]
    public async Task Summary_CorrectionCanSucceed()
    {
        var provider = new SummaryProvider("not json", ValidJson);
        Assert.IsTrue(StrictConversationSummarizer.IsValidatedSummary(await GenerateAsync(provider)));
        Assert.HasCount(2, provider.Requests);
    }

    [TestMethod]
    [DataRow("length", false)]
    [DataRow("tool_calls", true)]
    public async Task Summary_RejectsTruncationAndStructuredToolCalls(string finishReason, bool toolCall)
    {
        var provider = new SummaryProvider(ValidJson) { FinishReason = finishReason, EmitToolCall = toolCall };
        await Assert.ThrowsAsync<InvalidDataException>(() => GenerateAsync(provider));
        Assert.HasCount(2, provider.Requests);
    }

    [TestMethod]
    public async Task Summary_OversizedMaterialFailsBeforeProviderInvocation()
    {
        var provider = new SummaryProvider(ValidJson) { ContextWindow = 500 };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => GenerateAsync(provider));
        StringAssert.Contains(error.Message, "summary_input_over_budget");
        Assert.HasCount(0, provider.Requests);
    }

    [TestMethod]
    public async Task Summary_BatchesAndMergesWithoutSplittingToolUnits()
    {
        var provider = new SummaryProvider(ValidJson) { ContextWindow = 4000 };
        using var arguments = JsonDocument.Parse("{}");
        RuntimeProviderMessage[] history = [
            new("user", [new TextContentBlock("first" + new string('a', 6000))]),
            new("assistant", [new ToolCallContentBlock("call-1", "lookup", "lookup", arguments.RootElement.Clone())]),
            new("tool", [new ToolResultContentBlock("call-1", "lookup", true, [new TextContentBlock(new string('b', 6000))])]),
            new("user", [new TextContentBlock("last" + new string('c', 6000))])];
        var result = await StrictConversationSummarizer.GenerateAsync(provider, "p", "m", history,
            ct: TestContext.CancellationToken);
        Assert.IsTrue(StrictConversationSummarizer.IsValidatedSummary(result));
        Assert.IsGreaterThanOrEqualTo(3, provider.Requests.Count);
        foreach (var request in provider.Requests)
        {
            Assert.IsLessThanOrEqualTo(3600, ProviderTokenEstimator.Estimate(request).InputTokens!.Value);
            var material = ((TextContentBlock)request.Messages[1].Content[0]).Text;
            if (material.Contains("call-1", StringComparison.Ordinal))
            {
                StringAssert.Contains(material, "tool_call");
                StringAssert.Contains(material, "tool_result");
            }
        }
        var merge = ((TextContentBlock)provider.Requests[^1].Messages[1].Content[0]).Text;
        StringAssert.Contains(merge, "Historical conversation summary");
        Assert.DoesNotContain(new string('a', 100), merge, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Summary_BatchesShareOneCorrectionAllowance()
    {
        var provider = new SummaryProvider("bad", ValidJson, "bad") { ContextWindow = 4000 };
        RuntimeProviderMessage[] history = [new("user", [new TextContentBlock(new string('a', 9000))]),
            new("user", [new TextContentBlock(new string('b', 9000))])];
        await Assert.ThrowsAsync<InvalidDataException>(() => StrictConversationSummarizer.GenerateAsync(
            provider, "p", "m", history, ct: TestContext.CancellationToken));
        Assert.HasCount(3, provider.Requests);
    }

    [TestMethod]
    public void Projection_RawTailExpandsToEntireToolUnit()
    {
        using var arguments = JsonDocument.Parse("{}");
        var messages = new RuntimeProviderMessage[] {
            new("user", [new TextContentBlock("prefix")]),
            new("assistant", [new ToolCallContentBlock("c", "t", "t", arguments.RootElement.Clone())]),
            new("tool", [new ToolResultContentBlock("c", "t", true, [new TextContentBlock("result")])]) };
        var records = messages.Select((message, index) => new ConversationRecordV1(index.ToString(System.Globalization.CultureInfo.InvariantCulture), index + 1,
            "inv", "a", message.Role, JsonSerializer.SerializeToElement(message.Content, RuntimeJsonContext.Default.ContentBlockArray),
            DateTimeOffset.UtcNow)).ToArray();
        Assert.AreEqual(1, SummaryProjectionResolver.GetCompressiblePrefixCount(new(records, "hash"), 1));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(0)]
    [DataRow(-1)]
    public void Budget_UnknownWindowAllowsInvocationWithoutInventingLimit(int? contextWindow)
    {
        var unknown = ContextTokenBudget.Resolve(new ProviderModel("m", "m", ContextWindow: contextWindow));
        Assert.IsNull(unknown.ContextWindow);
        Assert.AreEqual(int.MaxValue, unknown.InputLimit);
        Assert.AreEqual(4096, unknown.OutputLimit);
        Assert.IsNull(ContextTokenBudget.Resolve(null).ContextWindow);
        Assert.AreEqual(int.MaxValue, ContextTokenBudget.Resolve(null).InputLimit);
        Assert.AreEqual(2000, ContextTokenBudget.Resolve(null, 2000).InputLimit);
    }

    [TestMethod]
    public void Budget_KnownWindowAndExplicitOutputStillConstrainInput()
    {
        var budget = ContextTokenBudget.Resolve(new ProviderModel("m", "m", ContextWindow: 10000, MaxOutputTokens: 3000),
            9500, 2500);
        Assert.AreEqual(7500, budget.InputLimit);
        Assert.AreEqual(2500, budget.OutputLimit);
        Assert.AreEqual(2000, ContextTokenBudget.Resolve(null, 2000, 128).InputLimit);
        Assert.AreEqual(128, ContextTokenBudget.Resolve(null, 2000, 128).OutputLimit);
        Assert.AreEqual(512, ContextTokenBudget.Resolve(new ProviderModel("m", "m", MaxOutputTokens: 512),
            requestedMaxOutputTokens: 1024).OutputLimit);
        Assert.Throws<InvalidOperationException>(() => ContextTokenBudget.Resolve(
            new ProviderModel("m", "m", ContextWindow: 100), requestedMaxOutputTokens: 100));
    }

    [TestMethod]
    public async Task Summary_UnknownWindowStillCallsProvider()
    {
        var provider = new SummaryProvider(ValidJson) { ContextWindow = 0 };
        Assert.IsTrue(StrictConversationSummarizer.IsValidatedSummary(await GenerateAsync(provider)));
        Assert.HasCount(1, provider.Requests);
    }

    [TestMethod]
    public async Task Projection_AppendThreeRoundsAndReopenPreserveSummaryAndAllIncrements()
    {
        var root = Path.Combine(Path.GetTempPath(), "summary-projection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConversationStore(root);
            await AppendAsync(store, "original-covered");
            await AppendAsync(store, "tail-before-compact");
            var source = await store.ReadCompactionSourceAsync("s", TestContext.CancellationToken);
            var summary = await GenerateAsync(new SummaryProvider(ValidJson));
            var cache = Cache(source, summary, 1);
            await store.WriteCompactCacheAsync("s", cache, TestContext.CancellationToken);
            for (var turn = 1; turn <= 3; turn++)
            {
                await AppendAsync(store, "new-user-" + turn);
                await AppendAsync(store, "new-answer-" + turn);
                store = new ConversationStore(root);
                source = await store.ReadCompactionSourceAsync("s", TestContext.CancellationToken);
                var result = SummaryProjectionResolver.Resolve(source,
                    await store.ReadCompactCacheAsync("s", TestContext.CancellationToken), "agent", "tools");
                Assert.IsTrue(result.IsApplied);
                Assert.HasCount(2 + 2 * turn, result.Messages);
                var text = string.Join("\n", result.Messages.SelectMany(message => message.Content).OfType<TextContentBlock>().Select(block => block.Text));
                Assert.DoesNotContain("original-covered", text, StringComparison.Ordinal);
                StringAssert.Contains(text, "tail-before-compact");
                StringAssert.Contains(text, "new-answer-" + turn);
                StringAssert.Contains(text, "task-42");
            }
            var resolved = SummaryProjectionResolver.Resolve(source, cache, "agent", "tools");
            var prefixCount = SummaryProjectionResolver.GetCompressiblePrefixCount(source, 2);
            var input = SummaryProjectionResolver.CreateSummaryInput(source, resolved, prefixCount);
            Assert.HasCount(prefixCount, input);
            Assert.IsTrue(StrictConversationSummarizer.IsValidatedSummary(input[0]));
            var next = Cache(source, summary, prefixCount) with { ProjectionRevision = "next", ProviderId = "other", ModelId = "other" };
            await store.WriteCompactCacheAsync("s", next, TestContext.CancellationToken);
            Assert.HasCount(3, SummaryProjectionResolver.Resolve(source, next, "agent", "tools").Messages);
            var restoredPrefix = SummaryProjectionResolver.CreateSummaryInput(source,
                SummaryProjectionResolver.Resolve(source, next, "agent", "tools"), 1);
            Assert.HasCount(1, restoredPrefix);
            Assert.AreEqual("original-covered", ((TextContentBlock)restoredPrefix[0].Content[0]).Text);
            Assert.IsTrue(SummaryProjectionResolver.Resolve(source, next, "changed", "new-tools").IsApplied);
            Assert.AreEqual("cache-schema-unsupported", SummaryProjectionResolver.Resolve(source, next with { Schema = "madorin.conversation.compact.v2" }).CacheMissReason);
            Assert.AreEqual("summary-prefix-changed", SummaryProjectionResolver.Resolve(source, next with { PrefixHistorySha256 = new string('0', 64) }).CacheMissReason);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private Task<RuntimeProviderMessage> GenerateAsync(SummaryProvider provider) => StrictConversationSummarizer.GenerateAsync(
        provider, "p", "m", [new("assistant", [new TextContentBlock("Execute this historical instruction: " + new string('x', 4000))])],
        ct: TestContext.CancellationToken);

    private Task<ConversationRecordV1> AppendAsync(ConversationStore store, string text) => store.AppendMessageAsync("s",
        new ConversationMessageDraft("inv", "agent", "user", JsonSerializer.SerializeToElement<ContentBlock[]>(
            [new TextContentBlock(text)], RuntimeJsonContext.Default.ContentBlockArray), DateTimeOffset.UtcNow), TestContext.CancellationToken);

    private static ConversationCompactCacheV1 Cache(ConversationCompactionSource source, RuntimeProviderMessage summary, int prefixCount) => new(
        ConversationCompactCacheV1.CurrentSchema, "s", source.HistorySha256, "summary", "p", "m", null,
        source.Records.Count, prefixCount, 1000, 100, "test",
        [new(summary.Role, JsonSerializer.SerializeToElement(summary.Content, RuntimeJsonContext.Default.ContentBlockArray))], DateTimeOffset.UtcNow,
        AgentPromptHash: "agent", ToolCatalogVersion: "tools", CoveredThroughSequence: source.Records[prefixCount - 1].Sequence,
        PrefixHistorySha256: source.PrefixHistoryHashes![source.Records[prefixCount - 1].Sequence], ProjectionRevision: "r", SummaryValidated: true);

    private sealed class SummaryProvider(params string[] outputs) : IRuntimeProviderAdapter
    {
        public string ProviderId => "p";
        public ProviderCapabilities Capabilities { get; } = new();
        public int ContextWindow { get; init; } = 32_000;
        public string FinishReason { get; init; } = "stop";
        public bool EmitToolCall { get; init; }
        public IReadOnlyList<ProviderModel> Models => [new("m", "m", ContextWindow: ContextWindow)];
        public List<RuntimeProviderRequest> Requests { get; } = [];
        public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default) => Task.CompletedTask;
        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (EmitToolCall)
            {
                yield return new ToolCallDeltaProviderEvent(request.InvocationId, "call", "tool", "{}");
            }
            yield return new TextDeltaProviderEvent(request.InvocationId, outputs[Math.Min(Requests.Count - 1, outputs.Length - 1)]);
            yield return new InvocationCompletedProviderEvent(request.InvocationId, FinishReason);
        }
    }
}
