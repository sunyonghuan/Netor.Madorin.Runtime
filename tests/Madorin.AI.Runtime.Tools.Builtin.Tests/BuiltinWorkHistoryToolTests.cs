using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Madorin.AI.Runtime.Tools.Builtin.Work;

namespace Madorin.AI.Runtime.Tools.Builtin.Tests;

[TestClass]
public sealed class BuiltinWorkHistoryToolTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task GetRecent_WhenMissing_ReturnsFoundFalse()
    {
        var executor = new BuiltinWorkHistoryToolExecutor(
            recentPlanLookup: static (_, _) => Task.FromResult<WorkRecentPlanView?>(null));
        var result = await executor.ExecuteAsync(
            new ToolInvocation("c1", BuiltinToolRegistry.WorkGetRecentCompletedTaskPlanToolId, "gm", null, "{}", "r1", "s1"),
            TestContext.CancellationToken);
        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsFalse(output.RootElement.GetProperty("found").GetBoolean());
    }

    [TestMethod]
    public async Task GetRecent_WhenCompleted_ReturnsPlan()
    {
        var planJson = """{"planVersion":"1","goal":"g","steps":[{"stepId":"s1","goal":"do","targetAgentId":"worker","dependsOn":[]}]}""";
        var executor = new BuiltinWorkHistoryToolExecutor(
            recentPlanLookup: (_, _) => Task.FromResult<WorkRecentPlanView?>(
                new WorkRecentPlanView(planJson, "1", "Completed")));
        var result = await executor.ExecuteAsync(
            new ToolInvocation("c1", BuiltinToolRegistry.WorkGetRecentCompletedTaskPlanToolId, "gm", null, "{}", "r1", "s1"),
            TestContext.CancellationToken);
        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("found").GetBoolean());
        Assert.AreEqual("1", output.RootElement.GetProperty("planVersion").GetString());
    }

    [TestMethod]
    public async Task ChatHistory_WithoutLookup_FailsUnavailable()
    {
        var executor = new BuiltinWorkHistoryToolExecutor();
        var result = await executor.ExecuteAsync(
            new ToolInvocation("c1", BuiltinToolRegistry.WorkLoadPlanFromChatHistoryToolId, "gm", null, "{}", "r1", "s1"),
            TestContext.CancellationToken);
        Assert.IsFalse(result.Success);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual("transcript_unavailable", output.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task ChatHistory_ReturnsTranscriptMaterials()
    {
        var executor = new BuiltinWorkHistoryToolExecutor(
            transcriptLookup: (query, _) =>
            {
                Assert.AreEqual("s1", query.SessionId);
                Assert.AreEqual("chat_history", query.Source);
                return Task.FromResult(
                    new WorkTranscriptView(
                        query.SessionId,
                        query.Source,
                        [
                            new WorkTranscriptMessage("user", "boss", "要做销售报告", DateTimeOffset.UtcNow)
                        ]));
            });
        var result = await executor.ExecuteAsync(
            new ToolInvocation("c1", BuiltinToolRegistry.WorkLoadPlanFromChatHistoryToolId, "gm", null, "{}", "r1", "s1"),
            TestContext.CancellationToken);
        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(1, output.RootElement.GetProperty("count").GetInt32());
        Assert.AreEqual("要做销售报告", output.RootElement.GetProperty("messages")[0].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task Groupchat_RequiresMeetingId()
    {
        var executor = new BuiltinWorkHistoryToolExecutor(
            transcriptLookup: static (_, _) =>
                Task.FromResult(new WorkTranscriptView("m1", "groupchat", [])));
        var result = await executor.ExecuteAsync(
            new ToolInvocation("c1", BuiltinToolRegistry.WorkLoadPlanFromGroupchatToolId, "gm", null, "{}", "r1", "s1"),
            TestContext.CancellationToken);
        Assert.IsFalse(result.Success);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual("invalid_arguments", output.RootElement.GetProperty("error").GetString());
    }
}
