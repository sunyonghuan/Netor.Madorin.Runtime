using System.Security.Cryptography;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ToolBlobStreamingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task StreamingStore_RoundTripsInBoundedReadsAndKeepsSourceOpen()
    {
        var root = Path.Join(Path.GetTempPath(), $"madorin-blob-stream-{Guid.NewGuid():N}");
        try
        {
            await using var store = new BlobStagingStore(root, new RuntimeLimits(), 16 * 1024 * 1024);
            var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
            await using var source = new BoundedReadStream(bytes);
            var reference = await store.StoreAsync("run", source, "application/octet-stream", "run:run",
                DateTimeOffset.UtcNow.AddHours(1), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
                TestContext.CancellationToken);
            Assert.IsTrue(source.CanRead);
            Assert.IsLessThanOrEqualTo(128 * 1024, source.MaxRequestedBytes);
            await using var read = await store.OpenReadAsync(reference, TestContext.CancellationToken);
            Assert.AreEqual(bytes.LongLength, read.Length);
            Assert.AreEqual(reference.Sha256,
                Convert.ToHexString(await SHA256.HashDataAsync(read, TestContext.CancellationToken)).ToLowerInvariant());
            Assert.IsEmpty(Directory.GetFiles(Path.Join(root, "staging")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow("short")]
    [DataRow("long")]
    [DataRow("hash")]
    [DataRow("quota")]
    [DataRow("io")]
    [DataRow("cancel")]
    public async Task StreamingStore_FailureNeverPublishesAndCleansStaging(string problem)
    {
        var root = Path.Join(Path.GetTempPath(), $"madorin-blob-stream-{Guid.NewGuid():N}");
        try
        {
            var limits = new RuntimeLimits(MaxBlobBytes: problem == "quota" ? 2 : 1024);
            await using var store = new BlobStagingStore(root, limits, 16 * 1024 * 1024);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            await using Stream source = new BoundedReadStream([1, 2, 3], problem, cancel);
            long? expectedLength = problem == "short" ? 4 : problem == "long" ? 2 : null;
            try
            {
                await store.StoreAsync("run", source, "application/octet-stream", "run:run",
                    DateTimeOffset.UtcNow.AddHours(1), expectedLength,
                    problem == "hash" ? new string('0', 64) : null, cancel.Token);
                Assert.Fail("An invalid stream must not publish a Blob.");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or OperationCanceledException)
            {
                Assert.AreEqual(problem == "cancel", ex is OperationCanceledException);
            }

            Assert.IsEmpty(Directory.GetFiles(Path.Join(root, "staging")));
            Assert.IsEmpty(Directory.GetFiles(Path.Join(root, "blobs")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class BoundedReadStream(byte[] bytes, string? failure = null,
        CancellationTokenSource? cancellation = null) : MemoryStream(bytes, writable: false)
    {
        public int MaxRequestedBytes { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            MaxRequestedBytes = Math.Max(MaxRequestedBytes, buffer.Length);
            if (Position > 0)
            {
                if (failure == "io")
                {
                    throw new IOException("Simulated source read failure.");
                }

                if (failure == "cancel")
                {
                    cancellation!.Cancel();
                }
            }

            return base.ReadAsync(buffer, ct);
        }
    }
}
