using System.Text;
using Madorin.AI.Runtime.Cli.Tools;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class LongTaskReliabilityTests
{
    [TestMethod]
    public async Task Materialize_TextBlobKeepsVisiblePrefix()
    {
        var body = new string('中', 40_000);
        var bytes = Encoding.UTF8.GetBytes(body);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
        var reference = new BlobReference(
            hash,
            bytes.Length,
            hash,
            "application/json",
            "run",
            DateTimeOffset.UtcNow.AddHours(1));
        var result = new ToolResultContentBlock(
            "call-1",
            "mcli_fs_inspect",
            true,
            [new BlobRefContentBlock(reference)]);

        var materialized = await ToolResultPreview.MaterializeAsync(
            result,
            supportsFileBlobs: false,
            new MemoryBlobStore(bytes),
            maxInlineBytes: 512,
            CancellationToken.None);

        var text = Assert.ContainsSingle(materialized.Content) as TextContentBlock;
        Assert.IsNotNull(text);
        Assert.IsLessThanOrEqualTo(512, Encoding.UTF8.GetByteCount(text.Text));
        StringAssert.Contains(text.Text, "truncated");
        StringAssert.Contains(text.Text, bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task WorkspaceLock_DeadHolderIsRecovered()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "madorin-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var key = WorkspaceWriteLock.ComputeWorkspaceKey(
                WorkspaceWriteLock.NormalizeWorkspace(workspace));
            var lockDirectory = Path.Combine(Path.GetTempPath(), "madorin.ai.runtime", "workspace-locks");
            Directory.CreateDirectory(lockDirectory);
            var holderPath = Path.Combine(lockDirectory, key + ".lock.holder");
            await File.WriteAllTextAsync(
                holderPath,
                "runtimeInstanceId=dead\npid=2147483646\nstartedAt=2026-01-01T00:00:00.0000000Z\n");

            await using var first = await WorkspaceWriteLock.AcquireAsync(workspace, "runtime-a");
            Assert.IsFalse(File.Exists(holderPath) && (await File.ReadAllTextAsync(holderPath)).Contains("pid=2147483646"));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task AllowTools_GrantsListedToolAndRejectsOthers()
    {
        Assert.IsTrue(StandaloneToolAllowList.TryParse(
            "mcli_fs_read,mcli_fs_write",
            out var allowList,
            out var error));
        Assert.IsNull(error);
        Assert.IsNotNull(allowList);

        var workspace = Path.Combine(Path.GetTempPath(), "madorin-allow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var allowed = await allowList!.RequestPermissionAsync(
                CreateContext(workspace, "mcli_fs_read", "call-read"),
                CancellationToken.None);
            var denied = await allowList.RequestPermissionAsync(
                CreateContext(workspace, "mcli_process", "call-process"),
                CancellationToken.None);

            Assert.AreEqual(ToolAuthorizationDecision.Granted, allowed.Decision);
            Assert.IsNotNull(allowed.Grant);
            Assert.AreEqual(ToolAuthorizationDecision.Denied, denied.Decision);
            Assert.IsNull(denied.Grant);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static ToolPermissionConsentContext CreateContext(
        string workspace,
        string toolId,
        string callId)
    {
        var invocation = new ToolInvocation(
            callId,
            toolId,
            "agent-1",
            ParentAgentId: null,
            "{}",
            "run-1",
            "session-1",
            "invocation-1");
        var descriptor = new ToolDescriptor(
            toolId,
            "builtin",
            toolId,
            "test",
            "{}");
        return new ToolPermissionConsentContext(
            new ToolPermissionRequest(
                "permission-" + callId,
                invocation.AgentId,
                invocation.ParentAgentId,
                toolId,
                callId,
                "hash",
                "Low",
                "call-or-run",
                "required",
                CorrelationId: "correlation-" + callId,
                RunId: invocation.RunId),
            invocation,
            descriptor,
            ToolConsentScopeResolution.Success(
                new ToolRequestedScope(
                    "workspace:.",
                    "read",
                    [workspace],
                    [],
                    false,
                    false,
                    false,
                    [],
                    [],
                    false,
                    new NetworkPolicy(true, []),
                    true)),
            workspace);
    }

    private sealed class MemoryBlobStore(byte[] content) : IToolResultBlobStore
    {
        public Task<BlobReference> StoreAsync(
            string runId,
            ReadOnlyMemory<byte> stored,
            string contentType,
            string accessScope,
            DateTimeOffset expiresAt,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ValidateReferenceAsync(BlobReference reference, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ReadOnlyMemory<byte>> ReadAsync(
            BlobReference reference,
            int maxBytes,
            CancellationToken ct = default)
        {
            var count = Math.Min(maxBytes, content.Length);
            return Task.FromResult<ReadOnlyMemory<byte>>(content.AsMemory(0, count));
        }
    }
}
