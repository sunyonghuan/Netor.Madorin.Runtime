using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Modes.Work;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Madorin.AI.Runtime.Modes.Tests;

/// <summary>Tests for two-level step structure execution.</summary>
[TestClass]
public sealed class WorkSubStepExecutionTests
{
    [TestMethod]
    public void WorkPlanDraft_WithSubSteps_SerializesCorrectly()
    {
        // Arrange
        var subStep1 = new WorkPlanSubStepDraft(
            "1.1",
            "Write README",
            "Create a README.md file with project description",
            "worker-1");

        var subStep2 = new WorkPlanSubStepDraft(
            "1.2",
            "Review README",
            "Review the README for accuracy",
            "reviewer-1");

        var step = new WorkPlanStepDraft(
            "1",
            "Documentation",
            "Create and review project documentation",
            [subStep1, subStep2]);

        var plan = new WorkPlanDraft(
            "1",
            "Setup project documentation",
            [step]);

        // Assert
        Assert.AreEqual("1", plan.PlanVersion);
        Assert.AreEqual("Setup project documentation", plan.Goal);
        Assert.HasCount(1, plan.Steps);
        Assert.AreEqual("1", plan.Steps[0].StepId);
        Assert.AreEqual("Documentation", plan.Steps[0].Title);
        Assert.HasCount(2, plan.Steps[0].SubSteps);
        Assert.AreEqual("1.1", plan.Steps[0].SubSteps[0].SubStepId);
        Assert.AreEqual("Write README", plan.Steps[0].SubSteps[0].Goal);
        Assert.AreEqual("worker-1", plan.Steps[0].SubSteps[0].TargetAgentId);
    }

    [TestMethod]
    public void WorkPlanSubStepDraft_WithAllFields_InitializesCorrectly()
    {
        // Arrange & Act
        var subStep = new WorkPlanSubStepDraft(
            "2.3",
            "Deploy to staging",
            "Deploy the application to staging environment and verify",
            "devops-agent",
            ExpectedOutput: "Deployment successful, health check passed");

        // Assert
        Assert.AreEqual("2.3", subStep.SubStepId);
        Assert.AreEqual("Deploy to staging", subStep.Goal);
        Assert.AreEqual("Deploy the application to staging environment and verify", subStep.Instruction);
        Assert.AreEqual("devops-agent", subStep.TargetAgentId);
        Assert.AreEqual("Deployment successful, health check passed", subStep.ExpectedOutput);
    }

    [TestMethod]
    public void WorkStepSnapshot_IsSubStep_IdentifiesSubStepsCorrectly()
    {
        // Arrange - SubStep snapshot
        var subStepSnapshot = new WorkStepSnapshot(
            StepId: "1.1",
            PlanVersion: "1",
            SessionId: "session-1",
            RunId: "run-1",
            TargetAgentId: "worker-1",
            Goal: "Write README",
            Status: WorkStepLifecycleStatus.Pending,
            StepInputHash: "hash1",
            ParentStepId: "1",
            IsSubStep: true,
            StepIndex: 0,
            SubStepIndex: 0);

        // Arrange - Top-level step snapshot (hypothetical, not in DB)
        var topLevelSnapshot = new WorkStepSnapshot(
            StepId: "1",
            PlanVersion: "1",
            SessionId: "session-1",
            RunId: "run-1",
            TargetAgentId: "manager-1",
            Goal: "Documentation",
            Status: WorkStepLifecycleStatus.Pending,
            StepInputHash: "hash2",
            IsSubStep: false,
            StepIndex: 0);

        // Assert
        Assert.IsTrue(subStepSnapshot.IsSubStep);
        Assert.AreEqual("1", subStepSnapshot.ParentStepId);
        Assert.AreEqual(0, subStepSnapshot.SubStepIndex);
        Assert.IsFalse(topLevelSnapshot.IsSubStep);
        Assert.IsNull(topLevelSnapshot.ParentStepId);
        Assert.AreEqual(-1, topLevelSnapshot.SubStepIndex);
    }

    [TestMethod]
    public void WorkPlanStepDraft_DependsOn_EstablishesTopLevelDependency()
    {
        // Arrange
        var step1 = new WorkPlanStepDraft(
            "1",
            "Design",
            "Design the architecture",
            [new WorkPlanSubStepDraft("1.1", "Draft design", "Create design doc", "designer")]);

        var step2 = new WorkPlanStepDraft(
            "2",
            "Implementation",
            "Implement the design",
            [new WorkPlanSubStepDraft("2.1", "Code feature", "Write code", "coder")],
            DependsOn: ["1"]);

        var plan = new WorkPlanDraft("1", "Build feature", [step1, step2]);

        // Assert
        Assert.IsNull(plan.Steps[0].DependsOn);
        Assert.IsNotNull(plan.Steps[1].DependsOn);
        var dependsOn = plan.Steps[1].DependsOn!;
        Assert.HasCount(1, dependsOn);
        Assert.AreEqual("1", dependsOn[0]);
    }
}

