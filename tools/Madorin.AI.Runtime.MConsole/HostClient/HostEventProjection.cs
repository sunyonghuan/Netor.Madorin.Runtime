namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record HostEventProjection(
    string RuntimeInstanceId,
    string RunId,
    long Gsn,
    long RunSequence,
    string MessageType,
    DateTimeOffset Timestamp,
    string PayloadSummary);
