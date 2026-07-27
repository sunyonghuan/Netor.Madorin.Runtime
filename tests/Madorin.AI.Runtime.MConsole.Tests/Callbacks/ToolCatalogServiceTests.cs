using System.Text.Json;

using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.Callbacks;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Callbacks;

[TestClass]
public sealed class ToolCatalogServiceTests
{
    private static readonly string[] EchoRemoval = ["host.echo"];

    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task ReplacePatchAndRevoke_UseTypedClientApi()
    {
        var client = new FakeRuntimeClient();
        var service = new ToolCatalogService(client);
        var first = new ToolCatalogDraft("v1", [CreateTool("host.echo")]);
        var second = new ToolCatalogDraft("v2", [CreateTool("host.clock")]);

        var replaced = await service.ReplaceAsync(first, TestContext.CancellationToken);
        var patched = await service.PatchAsync(
            "v1",
            second,
            ["host.echo", "host.echo"],
            TestContext.CancellationToken);
        var revoked = await service.RevokeGrantAsync(
            "grant-a",
            "test complete",
            cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("v1", replaced.CatalogVersion);
        Assert.AreEqual("v2", patched.CatalogVersion);
        Assert.AreEqual("grant-a", revoked.GrantId);
        Assert.AreEqual("host.echo", client.LastCatalogReplace!.Tools.Single().ToolId);
        CollectionAssert.AreEqual(EchoRemoval, client.LastCatalogPatch!.Removals);
        Assert.AreEqual("test complete", client.LastGrantRevoke!.Reason);
    }

    [TestMethod]
    public async Task LoadDraftAsync_ReadsValidatedFile()
    {
        using var directory = new OwnedTestDirectory();
        var path = Path.Combine(directory.Path, "catalog.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "catalogVersion": "file-v1",
              "tools": [
                {
                  "toolId": "host.echo",
                  "displayName": "Echo",
                  "description": "Echo input",
                  "inputSchema": { "type": "object" },
                  "outputSchema": { "type": "object" },
                  "risk": 0,
                  "timeoutSeconds": 5,
                  "tags": [],
                  "capabilities": [],
                  "executionTarget": 1,
                  "requiresApproval": false,
                  "isIdempotent": true
                }
              ]
            }
            """,
            TestContext.CancellationToken);

        var draft = await ToolCatalogService.LoadDraftAsync(
            path,
            TestContext.CancellationToken);

        Assert.AreEqual("file-v1", draft.CatalogVersion);
        Assert.AreEqual("host.echo", draft.Tools.Single().ToolId);
    }

    [TestMethod]
    public void UpsertAndRemove_ProduceNewValidatedDrafts()
    {
        var initial = new ToolCatalogDraft("v1", [CreateTool("host.echo")]);

        var upserted = ToolCatalogService.Upsert(initial, CreateTool("host.clock"), "v2");
        var removed = ToolCatalogService.Remove(upserted, "host.echo", "v3");

        Assert.HasCount(2, upserted.Tools);
        Assert.AreEqual("v3", removed.CatalogVersion);
        Assert.AreEqual("host.clock", removed.Tools.Single().ToolId);
    }

    private static ToolCatalogItem CreateTool(string toolId)
    {
        using var input = JsonDocument.Parse("{\"type\":\"object\"}");
        using var output = JsonDocument.Parse("{\"type\":\"object\"}");
        return new ToolCatalogItem(
            toolId,
            toolId,
            "Test tool",
            input.RootElement.Clone(),
            output.RootElement.Clone(),
            ToolRiskLevel.Low,
            5,
            [],
            [],
            ToolExecutionTarget.Host,
            RequiresApproval: false,
            IsIdempotent: true);
    }
}
