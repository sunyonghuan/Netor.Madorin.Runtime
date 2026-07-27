namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record EventConsumptionResult(
    string RuntimeInstanceId,
    long InitialGsn,
    long LastConfirmedGsn,
    int ProcessedEventCount,
    string? TerminalMessageType);
