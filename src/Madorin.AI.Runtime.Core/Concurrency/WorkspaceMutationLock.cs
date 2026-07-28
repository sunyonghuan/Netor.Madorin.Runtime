using System.Diagnostics.CodeAnalysis;

namespace Madorin.AI.Runtime.Core.Concurrency;

/// <summary>
/// Serializes workspace mutations within one Runtime process.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The shared semaphore has process-lifetime ownership and must outlive every lease.")]
public sealed class WorkspaceMutationLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Waits asynchronously until the caller owns the workspace mutation lease.
    /// </summary>
    public async ValueTask<Lease> AcquireAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    /// <summary>
    /// Releases a workspace mutation lock exactly once.
    /// </summary>
    public sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
