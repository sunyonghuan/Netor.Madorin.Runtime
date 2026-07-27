namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record EventConsumptionOptions(
    TimeSpan ConfirmationDelay = default,
    bool EnableEarlyAcknowledgementDangerous = false);
