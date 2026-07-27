using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.Recovery;

public sealed record SessionRecoveryResult(
    SessionResumeResult Resume,
    SessionRehydrateResult Rehydrate,
    RunQueryResult? LatestRun);

public sealed class SessionRecoveryService(IRuntimeClient client)
{
    private readonly IRuntimeClient _client = client;

    public async Task<SessionRecoveryResult> ResumeAndRehydrateAsync(
        string sessionId,
        AgentDefinition[] definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(definitions);
        var resume = await _client.ResumeSessionAsync(sessionId, cancellationToken);
        if (!string.Equals(resume.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Resume returned another session.");
        }

        if (!resume.IsFullyRecoverable)
        {
            throw new InvalidDataException(
                "Session is not fully recoverable: " +
                string.Join(" | ", resume.RecoveryDiagnostics));
        }

        var rehydrate = await _client.RehydrateSessionAsync(
            new SessionRehydrateParameters(sessionId, definitions),
            cancellationToken);
        if (!string.Equals(rehydrate.SessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(rehydrate.Status, "Ready", StringComparison.Ordinal)
            || rehydrate.Mismatched.Length != 0)
        {
            throw new InvalidDataException(
                $"Session rehydrate failed with status '{rehydrate.Status}': " +
                string.Join(", ", rehydrate.Mismatched));
        }

        var latestRun = resume.LatestRun is { } run
            ? await _client.QueryRunAsync(run.RunId, cancellationToken)
            : null;
        return new SessionRecoveryResult(resume, rehydrate, latestRun);
    }
}
