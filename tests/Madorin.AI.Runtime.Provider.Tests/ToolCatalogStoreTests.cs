using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ToolCatalogStoreTests
{
    [TestMethod]
    public void Replace_NullLegacyDescription_IsNormalizedBeforeEstimation()
    {
        var store = CreateStore();
        var snapshot = store.ReplaceHostCatalog(new ToolCatalogReplaceRequest(
            "legacy-v1", [CreateHostTool("host.recall") with { Description = null! }]));

        Assert.IsTrue(snapshot.TryGetTool("host.recall", out var tool));
        Assert.IsNotNull(tool);
        Assert.AreEqual(string.Empty, tool.Description);
        var estimate = ProviderTokenEstimator.Estimate(new RuntimeProviderRequest(
            "invocation", "agent", "provider", "model", [], Tools: [tool]));
        Assert.IsGreaterThan(0, estimate.InputTokens!.Value);
    }

    [TestMethod]
    public void Estimate_NullLegacyDescription_MatchesEmptyDescription()
    {
        var tool = new ToolDescriptor("host.recall", "host", "Recall", null!, "{}");
        var request = new RuntimeProviderRequest("invocation", "agent", "provider", "model", [], Tools: [tool]);

        var estimate = ProviderTokenEstimator.Estimate(request);
        var expected = ProviderTokenEstimator.Estimate(request with
        {
            Tools = [tool with { Description = string.Empty }]
        });

        Assert.AreEqual(expected, estimate);
    }

    [TestMethod]
    public void ReplaceAndPatch_PreserveExistingInvocationSnapshot()
    {
        var store = CreateStore();
        var first = store.ReplaceHostCatalog(new ToolCatalogReplaceRequest(
            "host-v1",
            [CreateHostTool("host.search")]));

        var second = store.PatchHostCatalog(new ToolCatalogPatchRequest(
            "host-v1",
            "host-v2",
            [CreateHostTool("host.fetch")],
            ["host.search"]));

        Assert.AreEqual("host-v1", first.HostCatalogVersion);
        Assert.IsTrue(first.TryGetTool("host.search", out _));
        Assert.IsFalse(first.TryGetTool("host.fetch", out _));
        Assert.AreEqual("host-v2", second.HostCatalogVersion);
        Assert.IsFalse(second.TryGetTool("host.search", out _));
        Assert.IsTrue(second.TryGetTool("host.fetch", out _));
        Assert.AreNotEqual(first.EffectiveVersion, second.EffectiveVersion);
        Assert.IsTrue(first.TryGetTool(BuiltinToolRegistry.MemoryReadToolId, out _));
        Assert.IsTrue(second.TryGetTool(BuiltinToolRegistry.MemoryReadToolId, out _));
        Assert.IsTrue(first.TryGetTool(BuiltinToolRegistry.MemoryWriteToolId, out _));
        Assert.IsTrue(first.TryGetTool(BuiltinToolRegistry.MemoryAppendToolId, out _));
        Assert.IsTrue(first.Tools.Any(static tool =>
            tool.ToolId == BuiltinToolRegistry.MemoryWriteToolId));
        Assert.IsFalse(first.Tools.Any(static tool =>
            tool.ToolId == BuiltinToolRegistry.MemoryAppendToolId));
        Assert.IsFalse(first.Tools.Any(static tool =>
            tool.ToolId == BuiltinToolRegistry.FileListToolId));
        Assert.IsTrue(first.Tools.Any(static tool =>
            tool.ToolId == BuiltinToolRegistry.FileInspectToolId));
    }

    [TestMethod]
    public void Replace_RejectsReservedDuplicateAndConflictingVersions()
    {
        var store = CreateStore();
        var valid = new ToolCatalogReplaceRequest(
            "host-v1",
            [CreateHostTool("host.search")]);
        var first = store.ReplaceHostCatalog(valid);
        var repeated = store.ReplaceHostCatalog(valid);

        Assert.AreSame(first, repeated);
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.ReplaceHostCatalog(new ToolCatalogReplaceRequest(
                "host-v2",
                [CreateHostTool("mcli_fs_escape")])));
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.ReplaceHostCatalog(new ToolCatalogReplaceRequest(
                "host-v2",
                [CreateHostTool("host.same"), CreateHostTool("host.same")])));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.ReplaceHostCatalog(new ToolCatalogReplaceRequest(
                "host-v1",
                [CreateHostTool("host.other")])));
    }

    [TestMethod]
    public void Patch_RejectsStaleBaseAndOverlappingChanges()
    {
        var store = CreateStore();
        store.ReplaceHostCatalog(new ToolCatalogReplaceRequest(
            "host-v1",
            [CreateHostTool("host.search")]));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.PatchHostCatalog(new ToolCatalogPatchRequest(
                "stale-v0",
                "host-v2",
                [],
                [])));
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.PatchHostCatalog(new ToolCatalogPatchRequest(
                "host-v1",
                "host-v2",
                [CreateHostTool("host.search")],
                ["host.search"])));
    }

    [TestMethod]
    public void SchemaValidator_ValidatesDescriptorArgumentsAndResult()
    {
        var validator = new JsonSchemaToolValidator();
        var descriptor = new ToolDescriptor(
            "host.search",
            "host",
            "Search",
            "Searches data.",
            """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}""",
            """{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"],"additionalProperties":false}""");

        Assert.IsTrue(validator.ValidateDescriptor(descriptor).IsValid);
        Assert.IsTrue(validator.ValidateArguments(
            descriptor,
            ParseElement("""{"query":"madorin"}""")).IsValid);
        var invalidArguments = validator.ValidateArguments(
            descriptor,
            ParseElement("""{"unexpected":true}"""));
        Assert.IsFalse(invalidArguments.IsValid);
        Assert.Contains("Expected object fields: query", invalidArguments.Error ?? string.Empty);
        Assert.IsTrue(validator.ValidateResult(
            descriptor,
            ParseElement("""{"count":1}""")).IsValid);
        Assert.IsFalse(validator.ValidateResult(
            descriptor,
            ParseElement("""{"count":"one"}""")).IsValid);
        Assert.IsFalse(validator.ValidateDescriptor(
            descriptor with { InputSchemaJson = "{not-json" }).IsValid);
    }

    [TestMethod]
    public void SchemaValidator_ConcurrentInstances_UseIsolatedSchemaRegistries()
    {
        var results = new ToolSchemaValidationResult[128];

        _ = Parallel.For(0, results.Length, index =>
        {
            var validator = new JsonSchemaToolValidator();
            var descriptor = new ToolDescriptor(
                $"host.concurrent.{index}",
                "host",
                "Concurrent schema",
                "Validates concurrent schema construction.",
                $$"""{"$id":"https://madorin.invalid/schema/{{index}}/input","type":"object"}""",
                $$"""{"$id":"https://madorin.invalid/schema/{{index}}/output","type":"object"}""");

            results[index] = validator.ValidateDescriptor(descriptor);
        });

        Assert.IsTrue(results.All(result => result.IsValid));
    }

    private static ToolCatalogStore CreateStore() =>
        new(new BuiltinToolRegistry(), new JsonSchemaToolValidator());

    private static ToolCatalogItem CreateHostTool(string toolId)
    {
        var schema = ParseElement("""{"type":"object"}""");
        return new ToolCatalogItem(
            toolId,
            toolId,
            "Test tool",
            schema,
            schema,
            ToolRiskLevel.Low,
            30,
            [],
            [],
            ToolExecutionTarget.Host,
            RequiresApproval: false,
            IsIdempotent: true);
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
