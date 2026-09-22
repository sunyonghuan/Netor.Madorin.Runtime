namespace Madorin.AI.Runtime.Contracts;

/// <summary>Enqueues a mid-meeting user interrupt / supplement for the active Meeting session.</summary>
public sealed record MeetingInputEnqueueRequest(
    string SessionId,
    string RequestId,
    ContentBlock[] Content,
    string? RunId = null,
    DateTimeOffset? EnqueuedAt = null);

/// <summary>Result of enqueuing a meeting interrupt input.</summary>
public sealed record MeetingInputEnqueueResult(
    string SessionId,
    string RequestId,
    bool Accepted,
    string Status,
    string? Message = null);

/// <summary>Requests a cooperative pause at the next Meeting safe point.</summary>
public sealed record MeetingPauseRequest(
    string SessionId,
    string RequestId,
    string? Reason = null,
    string? RunId = null);

/// <summary>Result of a meeting pause request.</summary>
public sealed record MeetingPauseResult(
    string SessionId,
    string RequestId,
    bool Accepted,
    string Status,
    string? Message = null);

/// <summary>
/// Validates that a paused Meeting can continue and returns the durable resume snapshot.
/// The host must then start a continuation Run via <c>run.existing_session</c>
/// (Interrupted is terminal; resume never reopens the paused Run).
/// </summary>
public sealed record MeetingResumeRequest(
    string SessionId,
    string RequestId,
    string? Reason = null,
    string? PreviousRunId = null);

/// <summary>Result of a meeting resume readiness check.</summary>
public sealed record MeetingResumeResult(
    string SessionId,
    string RequestId,
    bool Accepted,
    string Status,
    MeetingResumeState? State = null,
    int? NextRoundIndex = null,
    string? Message = null);

/// <summary>Durable pending user input row consumed between Meeting rounds.</summary>
public sealed record MeetingPendingInput(
    string RequestId,
    string SessionId,
    string? RunId,
    ContentBlock[] Content,
    DateTimeOffset EnqueuedAt,
    string Status);
