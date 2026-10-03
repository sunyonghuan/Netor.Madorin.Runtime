using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin.Work;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class WorkSetPlanToolTests
{
    private static ToolInvocation CreateInvocation(string argumentsJson)
    {
        return new ToolInvocation(
            CallId: "call1",
            ToolId: "mcli_work_set_plan",
            AgentId: "test-agent",
            ParentAgentId: null,
            ArgumentsJson: argumentsJson,
            RunId: "run1",
            SessionId: "session1");
    }
    [TestMethod]
    public async Task ExecuteAsync_ValidPlan_ReturnsSuccess()
    {
        var executor = new BuiltinWorkSetPlanToolExecutor(
            defaultTargetAgentId: "worker1",
            maxStepsPerRun: 10);

        var planJson = """
            {
              "planVersion": "1",
              "goal": "Test goal",
              "steps": [
                {
                  "stepId": "step1",
                  "title": "Deliverable 1",
                  "description": "Create document",
                  "subSteps": [
                    {
                      "subStepId": "step1-create",
                      "goal": "Write document",
                      "instruction": "Create a document",
                      "targetAgentId": "worker1"
                    }
                  ]
                }
              ]
            }
            """;

        var invocation = CreateInvocation(planJson);
        var result = await executor.ExecuteAsync(invocation);

        Assert.IsTrue(result.Success);
        var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("ok").GetBoolean());
    }

    [TestMethod]
    public async Task ExecuteAsync_EmptyPlan_ReturnsFailure()
    {
        var executor = new BuiltinWorkSetPlanToolExecutor(
            defaultTargetAgentId: "worker1",
            maxStepsPerRun: 10);

        var planJson = """
            {
              "planVersion": "1",
              "goal": "Test goal",
              "steps": []
            }
            """;

        var invocation = CreateInvocation(planJson);
        var result = await executor.ExecuteAsync(invocation);

        Assert.IsFalse(result.Success);
        var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsFalse(output.RootElement.GetProperty("ok").GetBoolean());
    }

    [TestMethod]
    public async Task ExecuteAsync_TooManySteps_ReturnsFailure()
    {
        var executor = new BuiltinWorkSetPlanToolExecutor(
            defaultTargetAgentId: "worker1",
            maxStepsPerRun: 2);

        var planJson = """
            {
              "planVersion": "1",
              "goal": "Test goal",
              "steps": [
                {
                  "stepId": "step1",
                  "title": "Deliverable 1",
                  "description": "First",
                  "subSteps": [
                    {
                      "subStepId": "step1-sub",
                      "goal": "Do it",
                      "instruction": "Do it",
                      "targetAgentId": "worker1"
                    }
                  ]
                },
                {
                  "stepId": "step2",
                  "title": "Deliverable 2",
                  "description": "Second",
                  "subSteps": [
                    {
                      "subStepId": "step2-sub",
                      "goal": "Do it",
                      "instruction": "Do it",
                      "targetAgentId": "worker1"
                    }
                  ]
                },
                {
                  "stepId": "step3",
                  "title": "Deliverable 3",
                  "description": "Third (exceeds max)",
                  "subSteps": [
                    {
                      "subStepId": "step3-sub",
                      "goal": "Do it",
                      "instruction": "Do it",
                      "targetAgentId": "worker1"
                    }
                  ]
                }
              ]
            }
            """;

        var invocation = CreateInvocation(planJson);
        var result = await executor.ExecuteAsync(invocation);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task ExecuteAsync_InvalidJson_ReturnsFailure()
    {
        var executor = new BuiltinWorkSetPlanToolExecutor(
            defaultTargetAgentId: "worker1",
            maxStepsPerRun: 10);

        var invocation = CreateInvocation("{ invalid json");
        var result = await executor.ExecuteAsync(invocation);

        Assert.IsFalse(result.Success);
        var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsFalse(output.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual("invalid_arguments", output.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void CanExecute_CorrectToolId_ReturnsTrue()
    {
        var executor = new BuiltinWorkSetPlanToolExecutor();

        var result = executor.CanExecute("mcli_work_set_plan");

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void CanExecute_WrongToolId_ReturnsFalse()
    {
        var executor = new BuiltinWorkSetPlanToolExecutor();

        var result = executor.CanExecute("other_tool");

        Assert.IsFalse(result);
    }
}
