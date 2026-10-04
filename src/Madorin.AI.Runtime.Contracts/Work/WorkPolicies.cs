namespace Madorin.AI.Runtime.Contracts;

/// <summary>
/// Workflow limits and failure behavior for Work mode.
/// Defaults use lenient loop detection (steps 50/hard 500).
/// </summary>
public sealed record WorkflowPolicy(
    int MaxStepsPerRun = 50,
    int HardMaxStepsPerRun = 500,
    int MaxSubAgentJobs = 20,
    int MaxConcurrentSteps = 4,
    int StepTimeoutSeconds = 300,
    WorkStepFailurePolicy FailurePolicy = WorkStepFailurePolicy.Stop,
    int MaxRetriesPerStep = 2,
    int RetryBackoffMilliseconds = 1000,
    WorkLoopDetection LoopDetection = WorkLoopDetection.Lenient,
    bool RequireHumanConfirmationOnLoop = false,
    /// <summary>
    /// 为 true 时，计划发布后会话停在等待确认，下一轮明确批准才执行。
    /// </summary>
    bool RequirePlanConfirmation = false)
{
    public static WorkflowPolicy Default { get; } = new();

    public void Validate()
    {
        if (MaxStepsPerRun < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxStepsPerRun),
                "MaxStepsPerRun must be at least 1.");
        }

        if (HardMaxStepsPerRun < MaxStepsPerRun)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HardMaxStepsPerRun),
                "HardMaxStepsPerRun must be greater than or equal to MaxStepsPerRun.");
        }

        if (HardMaxStepsPerRun > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HardMaxStepsPerRun),
                "HardMaxStepsPerRun cannot exceed 500.");
        }

        if (MaxSubAgentJobs < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSubAgentJobs),
                "MaxSubAgentJobs must be at least 1.");
        }

        if (MaxConcurrentSteps < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentSteps),
                "MaxConcurrentSteps must be at least 1.");
        }

        if (StepTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StepTimeoutSeconds),
                "StepTimeoutSeconds must be at least 1.");
        }

        if (MaxRetriesPerStep < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetriesPerStep),
                "MaxRetriesPerStep cannot be negative.");
        }

        if (RetryBackoffMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetryBackoffMilliseconds),
                "RetryBackoffMilliseconds cannot be negative.");
        }
    }
}

/// <summary>How work-mode history is projected into manager and child invocations.</summary>
public sealed record WorkContextPolicy(
    bool PreservePlan = true,
    bool PreserveCurrentStep = true,
    bool PreserveToolResults = true,
    bool PreserveReasoning = false,
    int? MaxTokens = null,
    int? TailMessageCount = null)
{
    public static WorkContextPolicy Default { get; } = new();
}
