using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Madorin.AI.Runtime.Tools.Builtin.ModeShared;

namespace Madorin.AI.Runtime.Tools.Builtin.Tests;

[TestClass]
public sealed class BuiltinModeInteractionToolTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CheckPending_EmptyQueue_ReturnsNoPendingMessage()
    {
        var executor = new BuiltinModeInteractionToolExecutor(
            consumePending: static (_, _, _) =>
                Task.FromResult(new WorkPendingInputsView([])));
        var result = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.CheckPendingUserInputToolId,
            "{}");
        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("ok").GetBoolean());
        Assert.IsFalse(output.RootElement.GetProperty("hasPending").GetBoolean());
        Assert.IsTrue(output.RootElement.GetProperty("consumed").GetBoolean());
        Assert.AreEqual("没有新的用户输入", output.RootElement.GetProperty("message").GetString());
    }

    [TestMethod]
    public async Task CheckPending_ConsumesTexts()
    {
        var executor = new BuiltinModeInteractionToolExecutor(
            consumePending: static (_, _, _) =>
                Task.FromResult(new WorkPendingInputsView(["先改目标", "保留第一步"])));
        var result = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.CheckPendingUserInputToolId,
            "{}");
        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("hasPending").GetBoolean());
        Assert.IsTrue(output.RootElement.GetProperty("consumed").GetBoolean());
        Assert.AreEqual(2, output.RootElement.GetProperty("count").GetInt32());
        Assert.AreEqual("先改目标", output.RootElement.GetProperty("inputs")[0].GetString());
    }

    [TestMethod]
    public async Task CheckPending_MeetingPeek_UsesPeekMessages()
    {
        var executor = new BuiltinModeInteractionToolExecutor(
            consumePending: static (_, _, _) =>
                Task.FromResult(
                    new WorkPendingInputsView(
                        ["预算上限50"],
                        Consumed: false,
                        EmptyMessage: "没有未处理的用户插话",
                        PendingMessage: "存在未处理的用户插话，请先处理插话后再继续或提问。")));
        var result = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.CheckPendingUserInputToolId,
            "{}");
        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("hasPending").GetBoolean());
        Assert.IsFalse(output.RootElement.GetProperty("consumed").GetBoolean());
        Assert.AreEqual(
            "存在未处理的用户插话，请先处理插话后再继续或提问。",
            output.RootElement.GetProperty("message").GetString());
    }

    [TestMethod]
    public async Task AskUser_RequiresQuestion()
    {
        var executor = new BuiltinModeInteractionToolExecutor(
            askUser: static (_, _) => Task.FromResult(new AskUserToolResponse(true, "ok")));
        var result = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.AskUserToolId,
            "{}");
        Assert.IsFalse(result.Success);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual("invalid_arguments", output.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task AskUser_ReturnsHostAnswer()
    {
        var executor = new BuiltinModeInteractionToolExecutor(
            askUser: static (request, _) =>
            {
                Assert.AreEqual("用哪个市场？", request.Question);
                return Task.FromResult(new AskUserToolResponse(true, "只用CN市场"));
            });
        var result = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.AskUserToolId,
            """{"question":"用哪个市场？"}""");
        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("answered").GetBoolean());
        Assert.AreEqual("只用CN市场", output.RootElement.GetProperty("answer").GetString());
    }

    [TestMethod]
    public void Registry_ExposesSharedToolsToWorkAndMeeting_NotExpert()
    {
        var registry = new BuiltinToolRegistry();
        var snapshot = new ToolCatalogSnapshot("host", "effective", registry.GetTools());
        var expert = snapshot.FilterByMode("expert").Tools.Select(static t => t.ToolId).ToHashSet(StringComparer.Ordinal);
        var work = snapshot.FilterByMode("work", "general-manager").Tools.Select(static t => t.ToolId).ToHashSet(StringComparer.Ordinal);
        var lead = snapshot.FilterByMode("work", "project-lead").Tools.Select(static t => t.ToolId).ToHashSet(StringComparer.Ordinal);
        var meetingHost = snapshot.FilterByMode("meeting", "meeting-host").Tools.Select(static t => t.ToolId).ToHashSet(StringComparer.Ordinal);
        var meetingParticipant = snapshot.FilterByMode("meeting", "finance").Tools.Select(static t => t.ToolId).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(BuiltinToolRegistry.AskUserToolId, expert);
        Assert.DoesNotContain(BuiltinToolRegistry.CheckPendingUserInputToolId, expert);
        Assert.Contains(BuiltinToolRegistry.AskUserToolId, work);
        Assert.Contains(BuiltinToolRegistry.CheckPendingUserInputToolId, work);
        Assert.Contains(BuiltinToolRegistry.WorkGetRecentCompletedTaskPlanToolId, work);
        Assert.DoesNotContain(BuiltinToolRegistry.AskUserToolId, lead);
        Assert.DoesNotContain(BuiltinToolRegistry.WorkGetRecentCompletedTaskPlanToolId, lead);
        Assert.Contains(BuiltinToolRegistry.AskUserToolId, meetingHost);
        Assert.DoesNotContain(BuiltinToolRegistry.AskUserToolId, meetingParticipant);
    }

    private Task<ToolResult> ExecuteAsync(
        BuiltinModeInteractionToolExecutor executor,
        string toolId,
        string argumentsJson) =>
        executor.ExecuteAsync(
            new ToolInvocation(
                Guid.NewGuid().ToString("N"),
                toolId,
                "general-manager",
                ParentAgentId: null,
                argumentsJson,
                "run-1",
                "session-1"),
            TestContext.CancellationToken);
}
