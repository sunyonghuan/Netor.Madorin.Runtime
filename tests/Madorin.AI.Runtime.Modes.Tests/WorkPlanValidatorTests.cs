using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Modes.Work;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class WorkPlanValidatorTests
{
    private static WorkModeOptions CreateOptions(params string[] agentIds)
    {
        var agents = agentIds.Length > 0
            ? agentIds.Select(id => new AgentRef(id, "v1", "test prompt")).ToArray()
            : new[] { new AgentRef("worker1", "v1", "test prompt") };

        return new WorkModeOptions(
            generalManager: new AgentRef("gm", "v1", "test prompt"),
            availableAgents: agents,
            workflowPolicy: null,
            contextPolicy: null);
    }

    [TestMethod]
    public void ValidateOrThrow_ValidPlanWithSubSteps_Succeeds()
    {
        var plan = new WorkPlanDraft(
            PlanVersion: "1",
            Goal: "Test goal",
            Steps: new[]
            {
                new WorkPlanStepDraft(
                    StepId: "step1",
                    Title: "Deliverable 1",
                    Description: "Create document",
                    SubSteps: new[]
                    {
                        new WorkPlanSubStepDraft(
                            SubStepId: "step1-create",
                            Goal: "Write document",
                            Instruction: "Create a document",
                            TargetAgentId: "worker1",
                            ExpectedOutput: null,
                            Inputs: null)
                    },
                    DependsOn: null)
            });

        var options = CreateOptions("worker1");
        var result = WorkPlanValidator.ValidateOrThrow(plan, options);

        Assert.IsNotNull(result);
        Assert.AreEqual("1", result.PlanVersion);
    }

    [TestMethod]
    public void ValidateOrThrow_EmptySteps_ThrowsArgumentException()
    {
        var plan = new WorkPlanDraft(
            PlanVersion: "1",
            Goal: "Test goal",
            Steps: Array.Empty<WorkPlanStepDraft>());

        var options = CreateOptions("worker1");

        try
        {
            WorkPlanValidator.ValidateOrThrow(plan, options);
            Assert.Fail("Expected ArgumentException");
        }
        catch (ArgumentException)
        {
        }
    }

    [TestMethod]
    public void ValidateOrThrow_StepWithoutSubSteps_ThrowsArgumentException()
    {
        var plan = new WorkPlanDraft(
            PlanVersion: "1",
            Goal: "Test goal",
            Steps: new[]
            {
                new WorkPlanStepDraft(
                    StepId: "step1",
                    Title: "Deliverable 1",
                    Description: "Create document",
                    SubSteps: Array.Empty<WorkPlanSubStepDraft>(),
                    DependsOn: null)
            });

        var options = CreateOptions("worker1");

        try
        {
            WorkPlanValidator.ValidateOrThrow(plan, options);
            Assert.Fail("Expected ArgumentException");
        }
        catch (ArgumentException)
        {
        }
    }

    [TestMethod]
    public void ValidateOrThrow_DuplicateStepId_ThrowsArgumentException()
    {
        var plan = new WorkPlanDraft(
            PlanVersion: "1",
            Goal: "Test goal",
            Steps: new[]
            {
                new WorkPlanStepDraft(
                    StepId: "step1",
                    Title: "Deliverable 1",
                    Description: "First",
                    SubSteps: new[]
                    {
                        new WorkPlanSubStepDraft(
                            SubStepId: "step1-create",
                            Goal: "Create",
                            Instruction: "Create it",
                            TargetAgentId: "worker1",
                            ExpectedOutput: null,
                            Inputs: null)
                    },
                    DependsOn: null),
                new WorkPlanStepDraft(
                    StepId: "step1",
                    Title: "Deliverable 2",
                    Description: "Second (duplicate ID)",
                    SubSteps: new[]
                    {
                        new WorkPlanSubStepDraft(
                            SubStepId: "step1-verify",
                            Goal: "Verify",
                            Instruction: "Verify it",
                            TargetAgentId: "worker1",
                            ExpectedOutput: null,
                            Inputs: null)
                    },
                    DependsOn: null)
            });

        var options = CreateOptions("worker1");

        try
        {
            WorkPlanValidator.ValidateOrThrow(plan, options);
            Assert.Fail("Expected ArgumentException");
        }
        catch (ArgumentException)
        {
        }
    }

    [TestMethod]
    public void ValidateOrThrow_DuplicateSubStepId_ThrowsArgumentException()
    {
        var plan = new WorkPlanDraft(
            PlanVersion: "1",
            Goal: "Test goal",
            Steps: new[]
            {
                new WorkPlanStepDraft(
                    StepId: "step1",
                    Title: "Deliverable 1",
                    Description: "Create document",
                    SubSteps: new[]
                    {
                        new WorkPlanSubStepDraft(
                            SubStepId: "substep1",
                            Goal: "First substep",
                            Instruction: "Do first",
                            TargetAgentId: "worker1",
                            ExpectedOutput: null,
                            Inputs: null),
                        new WorkPlanSubStepDraft(
                            SubStepId: "substep1",
                            Goal: "Second substep (duplicate ID)",
                            Instruction: "Do second",
                            TargetAgentId: "worker1",
                            ExpectedOutput: null,
                            Inputs: null)
                    },
                    DependsOn: null)
            });

        var options = CreateOptions("worker1");

        try
        {
            WorkPlanValidator.ValidateOrThrow(plan, options);
            Assert.Fail("Expected ArgumentException");
        }
        catch (ArgumentException)
        {
        }
    }

    private static readonly string[] Step1Deps = ["step2"];
    private static readonly string[] Step2Deps = ["step1"];

    [TestMethod]
    public void ValidateOrThrow_CircularDependency_ThrowsArgumentException()
    {
        var plan = new WorkPlanDraft(
            PlanVersion: "1",
            Goal: "Test goal",
            Steps: new[]
            {
                new WorkPlanStepDraft(
                    StepId: "step1",
                    Title: "Step 1",
                    Description: "Depends on step2",
                    SubSteps: new[]
                    {
                        new WorkPlanSubStepDraft(
                            SubStepId: "step1-sub",
                            Goal: "Do work",
                            Instruction: "Do it",
                            TargetAgentId: "worker1",
                            ExpectedOutput: null,
                            Inputs: null)
                    },
                    DependsOn: Step1Deps),
                new WorkPlanStepDraft(
                    StepId: "step2",
                    Title: "Step 2",
                    Description: "Depends on step1 (circular)",
                    SubSteps: new[]
                    {
                        new WorkPlanSubStepDraft(
                            SubStepId: "step2-sub",
                            Goal: "Do work",
                            Instruction: "Do it",
                            TargetAgentId: "worker1",
                            ExpectedOutput: null,
                            Inputs: null)
                    },
                    DependsOn: Step2Deps)
            });

        var options = CreateOptions("worker1");

        try
        {
            WorkPlanValidator.ValidateOrThrow(plan, options);
            Assert.Fail("Expected ArgumentException");
        }
        catch (ArgumentException)
        {
        }
    }


    [TestMethod]
    public void ComputeStepInputHash_SameInputs_ReturnsSameHash()
    {
        var hash1 = WorkPlanValidator.ComputeStepInputHash("step1", "1", "goal text");
        var hash2 = WorkPlanValidator.ComputeStepInputHash("step1", "1", "goal text");

        Assert.AreEqual(hash1, hash2);
    }

    [TestMethod]
    public void ComputeStepInputHash_DifferentInputs_ReturnsDifferentHash()
    {
        var hash1 = WorkPlanValidator.ComputeStepInputHash("step1", "1", "goal text");
        var hash2 = WorkPlanValidator.ComputeStepInputHash("step1", "1", "different goal");

        Assert.AreNotEqual(hash1, hash2);
    }

    [TestMethod]
    public void NormalizePlanRevision_FirstPlan_SetsVersionToOne()
    {
        var plan = new WorkPlanDraft(
            PlanVersion: "draft",
            Goal: "Test",
            Steps: new[]
            {
                new WorkPlanStepDraft(
                    StepId: "step1",
                    Title: "Step",
                    Description: "Desc",
                    SubSteps: new[]
                    {
                        new WorkPlanSubStepDraft(
                            SubStepId: "sub1",
                            Goal: "Goal",
                            Instruction: "Inst",
                            TargetAgentId: "worker1",
                            ExpectedOutput: null,
                            Inputs: null)
                    },
                    DependsOn: null)
            });

        var normalized = WorkPlanValidator.NormalizePlanRevision(plan, latestPublishedPlanVersion: null);

        Assert.AreEqual("1", normalized.PlanVersion);
        Assert.IsNull(normalized.PreviousPlanVersion);
    }

    [TestMethod]
    public void NormalizePlanRevision_SecondPlan_IncrementsVersion()
    {
        var plan = new WorkPlanDraft(
            PlanVersion: "draft",
            Goal: "Test",
            Steps: new[]
            {
                new WorkPlanStepDraft(
                    StepId: "step1",
                    Title: "Step",
                    Description: "Desc",
                    SubSteps: new[]
                    {
                        new WorkPlanSubStepDraft(
                            SubStepId: "sub1",
                            Goal: "Goal",
                            Instruction: "Inst",
                            TargetAgentId: "worker1",
                            ExpectedOutput: null,
                            Inputs: null)
                    },
                    DependsOn: null)
            });

        var normalized = WorkPlanValidator.NormalizePlanRevision(plan, latestPublishedPlanVersion: "1");

        Assert.AreEqual("2", normalized.PlanVersion);
        Assert.AreEqual("1", normalized.PreviousPlanVersion);
    }
}
