using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Madorin.AI.Runtime.Tools.Builtin.Work;

namespace Madorin.AI.Runtime.Tools.Builtin.Tests;

[TestClass]
public sealed class BuiltinWorkTemplateToolTests
{
    private string _userRoot = null!;
    private string _systemRoot = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _userRoot = Path.Combine(Path.GetTempPath(), $"madorin-work-tpl-user-{Guid.NewGuid():N}");
        _systemRoot = Path.Combine(Path.GetTempPath(), $"madorin-work-tpl-sys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_userRoot);
        Directory.CreateDirectory(_systemRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        DeleteQuietly(_userRoot);
        DeleteQuietly(_systemRoot);
    }

    [TestMethod]
    public async Task List_EmptyRoots_ReturnsOkEmptyTemplates()
    {
        var executor = CreateExecutor();
        var result = await ExecuteAsync(executor, BuiltinToolRegistry.WorkListPlanTemplatesToolId, "{}");
        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(0, output.RootElement.GetProperty("templates").GetArrayLength());
    }

    [TestMethod]
    public async Task Load_PathOutsideRoots_ReturnsPathNotAllowed()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"madorin-work-tpl-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            var executor = CreateExecutor();
            var args = $$"""{"path":{{JsonEncode(outside)}}}""";
            var result = await ExecuteAsync(
                executor,
                BuiltinToolRegistry.WorkLoadPlanFromTemplateToolId,
                args);
            Assert.IsFalse(result.Success);
            using var output = JsonDocument.Parse(result.OutputJson!);
            Assert.IsFalse(output.RootElement.GetProperty("ok").GetBoolean());
            Assert.AreEqual("path_not_allowed", output.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            DeleteQuietly(outside);
        }
    }

    [TestMethod]
    public async Task Load_MissingTemplateMd_ReturnsTemplateNotFound()
    {
        var templateDir = Path.Combine(_userRoot, "empty-template");
        Directory.CreateDirectory(templateDir);
        var executor = CreateExecutor();
        var args = $$"""{"path":{{JsonEncode(templateDir)}}}""";
        var result = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.WorkLoadPlanFromTemplateToolId,
            args);
        Assert.IsFalse(result.Success);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual("template_not_found", output.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task ListLoadRead_ValidTemplate_RoundTripsContent()
    {
        var templateDir = await CreateValidTemplateAsync("monthly-report");
        var executor = CreateExecutor();

        var list = await ExecuteAsync(executor, BuiltinToolRegistry.WorkListPlanTemplatesToolId, "{}");
        Assert.IsTrue(list.Success, list.Error);
        using (var listOutput = JsonDocument.Parse(list.OutputJson!))
        {
            var templates = listOutput.RootElement.GetProperty("templates");
            Assert.AreEqual(1, templates.GetArrayLength());
            Assert.AreEqual("monthly-report", templates[0].GetProperty("name").GetString());
        }

        var loadArgs = $$"""{"path":{{JsonEncode(templateDir)}}}""";
        var load = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.WorkLoadPlanFromTemplateToolId,
            loadArgs);
        Assert.IsTrue(load.Success, load.Error);
        using (var loadOutput = JsonDocument.Parse(load.OutputJson!))
        {
            Assert.IsTrue(loadOutput.RootElement.GetProperty("ok").GetBoolean());
            Assert.AreEqual("使用说明", loadOutput.RootElement.GetProperty("instructions").GetString());
            Assert.IsTrue(
                loadOutput.RootElement.GetProperty("planSkeleton").GetString()!
                    .Contains("计划骨架", StringComparison.Ordinal));
            var files = loadOutput.RootElement.GetProperty("files")
                .EnumerateArray()
                .Select(static item => item.GetProperty("relativePath").GetString())
                .ToArray();
            CollectionAssert.Contains(files, "TEMPLATE.md");
            CollectionAssert.Contains(files, "references/checklist.md");
        }

        var readArgs = $$"""{"path":{{JsonEncode(templateDir)}},"relativePath":"references/checklist.md"}""";
        var read = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.WorkReadTemplateFileToolId,
            readArgs);
        Assert.IsTrue(read.Success, read.Error);
        using var readOutput = JsonDocument.Parse(read.OutputJson!);
        Assert.AreEqual("check-1", readOutput.RootElement.GetProperty("content").GetString());
    }

    [TestMethod]
    public async Task SetPlan_NativeDraft_ReturnsOkPlan()
    {
        var executor = CreateExecutor();
        var setPlan = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.WorkSetPlanToolId,
            """
            {
              "planVersion":"1",
              "goal":"Ship report",
              "steps":[
                {"stepId":"s1","goal":"Write report","targetAgentId":"worker","dependsOn":[]}
              ]
            }
            """);
        Assert.IsTrue(setPlan.Success, setPlan.Error);
        using var output = JsonDocument.Parse(setPlan.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual("1", output.RootElement.GetProperty("planVersion").GetString());
        Assert.AreEqual(1, output.RootElement.GetProperty("stepCount").GetInt32());
        Assert.AreEqual("s1", output.RootElement.GetProperty("plan").GetProperty("steps")[0].GetProperty("stepId").GetString());
    }

    [TestMethod]
    public async Task SetPlan_MainSteps_MapsHierarchicalSteps()
    {
        var executor = new BuiltinWorkTemplateToolExecutor(
            _userRoot,
            _systemRoot,
            defaultTargetAgentId: "worker");
        var setPlan = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.WorkSetPlanToolId,
            """
            {
              "goal":"Monthly report",
              "main_steps":[
                {
                  "title":"Collect",
                  "sub_steps":[
                    {"title":"Read sales","acceptance_criteria":"totals present","targetAgentId":"worker"},
                    {"title":"Read refunds","acceptance_criteria":"refunds present"}
                  ]
                }
              ]
            }
            """);
        Assert.IsTrue(setPlan.Success, setPlan.Error);
        using var output = JsonDocument.Parse(setPlan.OutputJson!);
        Assert.AreEqual(3, output.RootElement.GetProperty("stepCount").GetInt32());
        var steps = output.RootElement.GetProperty("plan").GetProperty("steps");
        Assert.AreEqual("m1", steps[0].GetProperty("stepId").GetString());
        Assert.AreEqual("m1-s1", steps[1].GetProperty("stepId").GetString());
        Assert.AreEqual("m1", steps[1].GetProperty("parentStepId").GetString());
        Assert.AreEqual("worker", steps[1].GetProperty("targetAgentId").GetString());
        Assert.AreEqual("m1-s2", steps[2].GetProperty("stepId").GetString());
        Assert.AreEqual("worker", steps[2].GetProperty("targetAgentId").GetString());
        var salesGoal = steps[1].GetProperty("goal").GetString();
        var refundGoal = steps[2].GetProperty("goal").GetString();
        StringAssert.Contains(salesGoal, "Read sales");
        StringAssert.Contains(refundGoal, "Read refunds");
    }

    [TestMethod]
    public async Task SetPlan_MainSteps_DifferentAgents_StaySeparateDispatches()
    {
        var executor = new BuiltinWorkTemplateToolExecutor(
            _userRoot,
            _systemRoot,
            defaultTargetAgentId: "worker");
        var setPlan = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.WorkSetPlanToolId,
            """
            {
              "goal":"Monthly report",
              "main_steps":[
                {
                  "title":"Collect",
                  "sub_steps":[
                    {"title":"Read sales","acceptance_criteria":"totals present","targetAgentId":"worker"},
                    {"title":"Audit","acceptance_criteria":"signed","targetAgentId":"reviewer"}
                  ]
                }
              ]
            }
            """);
        Assert.IsTrue(setPlan.Success, setPlan.Error);
        using var output = JsonDocument.Parse(setPlan.OutputJson!);
        Assert.AreEqual(3, output.RootElement.GetProperty("stepCount").GetInt32());
        var steps = output.RootElement.GetProperty("plan").GetProperty("steps");
        Assert.AreEqual("m1", steps[0].GetProperty("stepId").GetString());
        Assert.AreEqual("m1-s1", steps[1].GetProperty("stepId").GetString());
        Assert.AreEqual("m1-s2", steps[2].GetProperty("stepId").GetString());
        Assert.AreEqual("worker", steps[1].GetProperty("targetAgentId").GetString());
        Assert.AreEqual("reviewer", steps[2].GetProperty("targetAgentId").GetString());
    }

    [TestMethod]
    public async Task SetPlan_Placeholder_IsRejected()
    {
        var executor = CreateExecutor();
        var setPlan = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.WorkSetPlanToolId,
            """{"planVersion":"1","goal":"{{month}} report","steps":[{"stepId":"s1","goal":"x","targetAgentId":"worker"}]}""");
        Assert.IsFalse(setPlan.Success);
        using var output = JsonDocument.Parse(setPlan.OutputJson!);
        Assert.AreEqual("invalid_plan", output.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Dispatch_WithoutPlan_ReturnsPlanNotSet()
    {
        var executor = new BuiltinWorkTemplateToolExecutor(
            _userRoot,
            _systemRoot,
            planLookup: static (_, _) => Task.FromResult<WorkSessionPlanView?>(null));
        var dispatch = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.WorkDispatchStepToolId,
            """{"stepIndex":1,"subStepIndex":1,"instruction":"do it"}""");
        Assert.IsFalse(dispatch.Success);
        using var output = JsonDocument.Parse(dispatch.OutputJson!);
        Assert.AreEqual("plan_not_set", output.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Dispatch_WaitingConfirmation_IsRejected()
    {
        var planJson =
            """
            {"planVersion":"1","goal":"g","steps":[{"stepId":"s1","goal":"do","targetAgentId":"worker","dependsOn":[]}]}
            """;
        var executor = new BuiltinWorkTemplateToolExecutor(
            _userRoot,
            _systemRoot,
            planLookup: (_, _) => Task.FromResult<WorkSessionPlanView?>(
                new WorkSessionPlanView(planJson, "WaitingForPlanConfirmation")));
        var dispatch = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.WorkDispatchStepToolId,
            """{"stepIndex":1,"subStepIndex":1,"instruction":"run s1"}""");
        Assert.IsFalse(dispatch.Success);
        using var output = JsonDocument.Parse(dispatch.OutputJson!);
        Assert.AreEqual("plan_not_confirmed", output.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Dispatch_ConfirmedPlan_ReturnsStepTarget()
    {
        var planJson =
            """
            {"planVersion":"1","goal":"g","steps":[{"stepId":"s1","goal":"do","targetAgentId":"worker","dependsOn":[]}]}
            """;
        var executor = new BuiltinWorkTemplateToolExecutor(
            _userRoot,
            _systemRoot,
            planLookup: (_, _) => Task.FromResult<WorkSessionPlanView?>(
                new WorkSessionPlanView(planJson, "Executing")));
        var dispatch = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.WorkDispatchStepToolId,
            """{"stepIndex":1,"subStepIndex":1,"instruction":"run s1"}""");
        Assert.IsTrue(dispatch.Success, dispatch.Error);
        using var output = JsonDocument.Parse(dispatch.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("ok").GetBoolean());
        Assert.IsFalse(output.RootElement.GetProperty("executed").GetBoolean());
        Assert.AreEqual("s1", output.RootElement.GetProperty("stepId").GetString());
        Assert.AreEqual("worker", output.RootElement.GetProperty("targetAgentId").GetString());
    }

    [TestMethod]
    public async Task Dispatch_WithRunner_ReturnsExecutedOutput()
    {
        var planJson =
            """
            {"planVersion":"1","goal":"g","steps":[{"stepId":"s1","goal":"do","targetAgentId":"worker","dependsOn":[]}]}
            """;
        var executor = new BuiltinWorkTemplateToolExecutor(
            _userRoot,
            _systemRoot,
            planLookup: (_, _) => Task.FromResult<WorkSessionPlanView?>(
                new WorkSessionPlanView(planJson, "Executing")),
            dispatchRunner: (request, _) =>
            {
                Assert.AreEqual("s1", request.StepId);
                Assert.AreEqual("worker", request.TargetAgentId);
                Assert.AreEqual("run s1", request.Instruction);
                return Task.FromResult(
                    new WorkDispatchStepResult(
                        true,
                        "worker done",
                        ChildInvocationId: "child-1"));
            });
        var dispatch = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.WorkDispatchStepToolId,
            """{"stepIndex":1,"subStepIndex":1,"instruction":"run s1"}""");
        Assert.IsTrue(dispatch.Success, dispatch.Error);
        using var output = JsonDocument.Parse(dispatch.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("executed").GetBoolean());
        Assert.AreEqual("worker done", output.RootElement.GetProperty("outputText").GetString());
        Assert.AreEqual("child-1", output.RootElement.GetProperty("childInvocationId").GetString());
    }

    [TestMethod]
    public void Registry_CanLoadIntoToolCatalogStore()
    {
        var store = new Madorin.AI.Runtime.Services.Tools.ToolCatalogStore(
            new BuiltinToolRegistry(),
            new Madorin.AI.Runtime.Services.Tools.JsonSchemaToolValidator());
        var snapshot = store.CaptureSnapshot();
        Assert.Contains(BuiltinToolRegistry.WorkListPlanTemplatesToolId,
            snapshot.Tools.Select(static tool => tool.ToolId));
        Assert.DoesNotContain(
            BuiltinToolRegistry.WorkListPlanTemplatesToolId,
            snapshot.FilterByMode("expert").Tools.Select(static tool => tool.ToolId));
    }

    [TestMethod]
    public void Registry_MarksWorkToolsForWorkModeOnly()
    {
        var registry = new BuiltinToolRegistry();
        var snapshot = new ToolCatalogSnapshot("host", "effective", registry.GetTools());
        var expertIds = snapshot.FilterByMode("expert").Tools.Select(static t => t.ToolId).ToHashSet(StringComparer.Ordinal);
        var workIds = snapshot.FilterByMode("work").Tools.Select(static t => t.ToolId).ToHashSet(StringComparer.Ordinal);
        var leadIds = snapshot.FilterByMode("work", "project-lead").Tools
            .Select(static t => t.ToolId)
            .ToHashSet(StringComparer.Ordinal);

        var workerIds = snapshot.FilterByMode("work", "worker").Tools
            .Select(static t => t.ToolId)
            .ToHashSet(StringComparer.Ordinal);
        var gmIds = snapshot.FilterByMode("work", "general-manager").Tools
            .Select(static t => t.ToolId)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(BuiltinToolRegistry.WorkListPlanTemplatesToolId, expertIds);
        Assert.DoesNotContain(BuiltinToolRegistry.WorkLoadPlanFromTemplateToolId, expertIds);
        Assert.DoesNotContain(BuiltinToolRegistry.WorkReadTemplateFileToolId, expertIds);
        Assert.Contains(BuiltinToolRegistry.WorkListPlanTemplatesToolId, workIds);
        Assert.Contains(BuiltinToolRegistry.WorkReadTemplateFileToolId, workIds);
        Assert.DoesNotContain(BuiltinToolRegistry.WorkListPlanTemplatesToolId, leadIds);
        Assert.Contains(BuiltinToolRegistry.WorkReadTemplateFileToolId, leadIds);
        Assert.Contains(BuiltinToolRegistry.WorkDispatchStepToolId, leadIds);
        Assert.Contains(BuiltinToolRegistry.WorkSetPlanToolId, gmIds);
        Assert.DoesNotContain(BuiltinToolRegistry.WorkSetPlanToolId, workerIds);
        Assert.DoesNotContain(BuiltinToolRegistry.WorkDispatchStepToolId, workerIds);
    }

    private BuiltinWorkTemplateToolExecutor CreateExecutor() =>
        new(_userRoot, _systemRoot);

    private Task<ToolResult> ExecuteAsync(
        BuiltinWorkTemplateToolExecutor executor,
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

    private async Task<string> CreateValidTemplateAsync(string folderName)
    {
        var templateDir = Path.Combine(_userRoot, folderName);
        Directory.CreateDirectory(Path.Combine(templateDir, "references"));
        var markdown =
            """
            ---
            name: monthly-report
            description: 月报模板
            category: reporting
            version: "1.0"
            ---
            使用说明

            ## 计划骨架
            - 收集数据
            - 写报告
            """;
        await File.WriteAllTextAsync(
            Path.Combine(templateDir, "TEMPLATE.md"),
            markdown,
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(templateDir, "references", "checklist.md"),
            "check-1",
            TestContext.CancellationToken);
        return templateDir;
    }

    private static void DeleteQuietly(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string JsonEncode(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            + "\"";
}
