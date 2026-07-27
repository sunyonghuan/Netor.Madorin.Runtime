using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Recovery;

public sealed record CredentialUpdateDraft(
    string RunId,
    string ProviderId,
    string CredentialEnvironmentVariable,
    string? ProviderProfileId = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record CredentialUpdateAudit(
    string RunId,
    string ProviderId,
    string CredentialEnvironmentVariable,
    string? ProviderProfileId,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CompletedAt);

public sealed class CredentialUpdateService(
    IRuntimeClient client,
    ProcessRunner? processRunner = null,
    TimeProvider? timeProvider = null)
{
    private const string RuntimeSecretEnvironmentVariable = "MADORIN_AI_RUNTIME_SECRET";

    private readonly IRuntimeClient _client = client;
    private readonly ProcessRunner _processRunner = processRunner ?? new ProcessRunner();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<CredentialUpdateAudit> UpdateViaSdkAsync(
        CredentialUpdateDraft draft,
        CancellationToken cancellationToken = default)
    {
        Validate(draft);
        var credential = ReadRequiredEnvironmentVariable(
            draft.CredentialEnvironmentVariable);
        await _client.UpdateCredentialsAsync(
            new CredentialsUpdateParameters(
                draft.RunId,
                draft.ProviderId,
                credential,
                draft.ProviderProfileId,
                draft.ExpiresAt),
            cancellationToken);
        return CreateAudit(draft);
    }

    public async Task<ExecutionResult> UpdateViaCtlAsync(
        string executablePath,
        string workspace,
        string runtimeSecret,
        CredentialUpdateDraft draft,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeSecret);
        Validate(draft);
        var credential = ReadRequiredEnvironmentVariable(
            draft.CredentialEnvironmentVariable);
        var arguments = new List<string>
        {
            "ctl", "credential", "update",
            "--workspace", Path.GetFullPath(workspace),
            "--run", draft.RunId,
            "--provider", draft.ProviderId,
            "--api-key-env", draft.CredentialEnvironmentVariable,
            "--json",
        };
        if (!string.IsNullOrWhiteSpace(draft.ProviderProfileId))
        {
            arguments.Add("--profile");
            arguments.Add(draft.ProviderProfileId);
        }

        if (draft.ExpiresAt is { } expiresAt)
        {
            arguments.Add("--expires-at");
            arguments.Add(expiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        return await _processRunner.RunAsync(
            executablePath,
            arguments,
            workingDirectory: workspace,
            environment: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [RuntimeSecretEnvironmentVariable] = runtimeSecret,
                [draft.CredentialEnvironmentVariable] = credential,
            },
            timeout: timeout,
            cancellationToken: cancellationToken);
    }

    private CredentialUpdateAudit CreateAudit(CredentialUpdateDraft draft) => new(
        draft.RunId,
        draft.ProviderId,
        draft.CredentialEnvironmentVariable,
        draft.ProviderProfileId,
        draft.ExpiresAt,
        _timeProvider.GetUtcNow());

    private static string ReadRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"Credential environment variable '{name}' is missing or empty.");
    }

    private static void Validate(CredentialUpdateDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.CredentialEnvironmentVariable);
        if (draft.CredentialEnvironmentVariable.Contains('=', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Credential environment variable name cannot contain '='.",
                nameof(draft));
        }
    }
}
