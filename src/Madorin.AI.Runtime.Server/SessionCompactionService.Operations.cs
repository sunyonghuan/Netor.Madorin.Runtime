using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Server;

internal sealed partial class SessionCompactionService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OperationEntry> _operations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();

    public async Task<SessionCompactionResult?> CompactAsync(
        string sessionId, SessionCompactionOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ValidateOptions(options);
        var operationId = options.OperationId ?? Guid.NewGuid().ToString("N");
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (operationId.Length > 128)
        {
            throw new ArgumentException("OperationId must not exceed 128 characters.");
        }

        options = options with { OperationId = operationId };
        var key = OperationPath(sessionId, operationId);
        var fingerprint = HashId(JsonSerializer.Serialize(new SessionCompactionRequest(sessionId,
            ToWireValue(options.Strategy), options.ProviderId, options.ModelId, options.KeepLastTokens,
            options.DryRun, options.Force, RawTailSize: options.RawTailSize), RuntimeJsonContext.Default.SessionCompactionRequest));
        var persisted = await ReadOperationAsync(sessionId, operationId, ct).ConfigureAwait(false);
        if (persisted?.RequestFingerprint is { } persistedFingerprint && persistedFingerprint != fingerprint)
        {
            throw new InvalidOperationException("operation_id_conflict: OperationId is already bound to a different compaction request.");
        }
        var operation = _operations.GetOrAdd(key, _ => new OperationEntry(sessionId, operationId, fingerprint, new Lazy<Task<SessionCompactionResult?>>(
            () => ExecuteOwnedOperationAsync(sessionId, options, fingerprint), LazyThreadSafetyMode.ExecutionAndPublication)));
        if (!string.Equals(fingerprint, operation.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("operation_id_conflict: OperationId is already bound to a different compaction request.");
        }
        // A disconnected waiter does not cancel an already accepted, potentially billable operation.
        try
        {
            return await operation.Task.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("operation_id_conflict:", StringComparison.Ordinal))
        {
            _operations.TryRemove(new KeyValuePair<string, OperationEntry>(key, operation));
            throw;
        }
    }

    public IDisposable EnterMutation(string sessionId)
    {
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            throw new InvalidOperationException("session_busy: context compaction or Session mutation is running.");
        }
        return new SessionLease(gate);
    }

    private async Task<SessionCompactionResult?> ExecuteOwnedOperationAsync(
        string sessionId, SessionCompactionOptions options, string fingerprint)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var ct = deadline.Token;
        using var lease = EnterMutation(sessionId);
        var previous = await ReadOperationAsync(sessionId, options.OperationId!, ct).ConfigureAwait(false);
        if (previous is not null)
        {
            if (previous.RequestFingerprint is not null && previous.RequestFingerprint != fingerprint)
            {
                throw new InvalidOperationException("operation_id_conflict: OperationId is already bound to a different compaction request.");
            }
            previous = await RecoverOperationAsync(previous, ct).ConfigureAwait(false);
            if (previous.Result is { } saved)
            {
                return FromDto(saved);
            }
            throw new InvalidOperationException(previous.ReasonCode ?? "compaction_operation_failed");
        }

        await using (var connection = new SqliteConnection(_databaseConnectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var repository = new SqliteSessionRepository(connection);
            if ((await repository.ListRunSnapshotsAsync(sessionId, nonTerminalOnly: true, ct).ConfigureAwait(false)).Count != 0)
            {
                throw new InvalidOperationException("session_busy: wait for the active Run to finish before compacting.");
            }
        }

        var state = new SessionContextStatusDto(sessionId, options.OperationId, "Running", null, null,
            null, null, null, "unknown", true, null, DateTimeOffset.UtcNow, RequestFingerprint: fingerprint);
        await WriteOperationAsync(state, ct).ConfigureAwait(false);
        try
        {
            var result = await CompactCoreAsync(sessionId, options, ct).ConfigureAwait(false);
            state = state with
            {
                OperationState = result is null ? "Failed" : "Succeeded",
                ProjectionRevision = result?.ProjectionRevision,
                CoveredThroughSequence = result?.CoveredThroughSequence,
                HistoryEstimatedTokens = result?.AfterEstimatedTokens,
                EstimateSource = result?.EstimateSource ?? "unknown",
                ReasonCode = result is null ? "session_not_found" : null,
                Result = result is null ? null : ToDto(result),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await WriteOperationAsync(state, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            // A deadline can race the atomic cache rename. Recover committed work before declaring failure.
            var recovered = await RecoverOperationAsync(state, CancellationToken.None).ConfigureAwait(false);
            if (recovered.Result is { } committed)
            {
                return FromDto(committed);
            }
            state = state with
            {
                OperationState = "Failed",
                ReasonCode = ex is OperationCanceledException ? "compaction_timeout" : ex.Message,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await WriteOperationAsync(state, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private string OperationPath(string sessionId, string operationId) => Path.Combine(
        _dataDirectory, "compaction-operations", HashId(sessionId), HashId(operationId) + ".json");

    private static string HashId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private async Task<SessionContextStatusDto?> ReadOperationAsync(string sessionId, string operationId, CancellationToken ct)
    {
        var path = OperationPath(sessionId, operationId);
        if (!File.Exists(path))
        {
            return null;
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync(stream, RuntimeJsonContext.Default.SessionContextStatusDto, ct)
            .ConfigureAwait(false);
    }

    private async Task WriteOperationAsync(SessionContextStatusDto state, CancellationToken ct)
    {
        var path = OperationPath(state.SessionId, state.OperationId!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, state, RuntimeJsonContext.Default.SessionContextStatusDto, ct)
                    .ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static SessionCompactionResultDto ToDto(SessionCompactionResult result) => new(
        result.SessionId, ToWireValue(result.Strategy), result.ProviderId, result.ModelId, result.KeepLastTokens,
        result.SourceMessageCount, result.ProjectedMessageCount, result.DroppedMessageCount,
        result.BeforeEstimatedTokens, result.AfterEstimatedTokens, result.EstimateSource,
        result.DryRun, result.CacheHit, result.CacheWritten, result.CacheMissReason,
        result.SelectionSource, result.SummaryGenerated, result.Outcome, result.OperationId,
        result.ProjectionRevision, result.CoveredThroughSequence);

    private static SessionCompactionResult FromDto(SessionCompactionResultDto result) => new(
        result.SessionId, ParseStrategy(result.Strategy), result.ProviderId, result.ModelId, result.KeepLastTokens,
        result.SourceMessageCount, result.ProjectedMessageCount, result.DroppedMessageCount,
        result.BeforeEstimatedTokens, result.AfterEstimatedTokens, result.EstimateSource,
        result.DryRun, result.CacheHit, result.CacheWritten, result.CacheMissReason,
        result.SelectionSource, result.SummaryGenerated, result.Outcome, result.OperationId,
        result.ProjectionRevision, result.CoveredThroughSequence);

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var operation in _operations.Values.Where(value => value.Task.IsValueCreated))
        {
            try
            {
                await operation.Task.Value.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Failures are persisted for status queries by the operation itself.
            }
        }
        _shutdown.Dispose();
    }

    private sealed class SessionLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private sealed record OperationEntry(string SessionId, string OperationId, string Fingerprint, Lazy<Task<SessionCompactionResult?>> Task);
}
