using System.Text;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Multimedia.Tests;

[TestClass]
public sealed class CoreProtocolTests
{
    [TestMethod]
    public void ParserRejectsMissingValueBeforeAnotherOption()
    {
        var allowed = new HashSet<string>(["prompt", "try-run"], StringComparer.Ordinal);
        var switches = new HashSet<string>(["try-run"], StringComparer.Ordinal);
        var error = Assert.ThrowsExactly<SkillException>(() => CommandLineParser.Parse(["--prompt", "--try-run"], allowed, switches));
        Assert.AreEqual("invalid_parameter", error.Code);
    }

    [TestMethod]
    public void WorkspaceRejectsTraversalAndSiblingWithSamePrefix()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.Root);
        Assert.ThrowsExactly<SkillException>(() => paths.Resolve("../escape.json"));
        Assert.ThrowsExactly<SkillException>(() => paths.Resolve(workspace.Root + "-sibling/file.json"));
    }

    [TestMethod]
    public async Task RequestReaderRejectsDuplicateKeysAndInvalidUtf8()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "job.json");
        var reader = new RequestFileReader(new WorkspacePaths(workspace.Root));
        await File.WriteAllTextAsync(path, "{\"request_id\":\"a\",\"request_id\":\"b\"}");
        await Assert.ThrowsExactlyAsync<SkillException>(async () => { using var document = await reader.ReadAsync("job.json", CancellationToken.None); });
        await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0x7b]);
        await Assert.ThrowsExactlyAsync<SkillException>(async () => { using var document = await reader.ReadAsync("job.json", CancellationToken.None); });
    }

    [TestMethod]
    public async Task CancelReaderIgnoresOversizeWrongIdAndInvalidFrames()
    {
        await using var invocation = new SkillInvocation { ResponseId = "req-active" };
        var frames = new string('x', 5000) + "\n" +
            "{\"schema_version\":42,\"type\":\"cancel\",\"request_id\":\"req-active\"}\n" +
            "{\"schema_version\":\"1.0\",\"type\":\"cancel\",\"request_id\":\"other\"}\n" +
            "{\"schema_version\":\"1.0\",\"type\":\"cancel\",\"request_id\":\"req-active\"}\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames));
        invocation.ListenForCancellation(input);
        Assert.IsTrue(invocation.HostCancelled);
        Assert.IsTrue(invocation.CancellationToken.IsCancellationRequested);
    }

    [TestMethod]
    public async Task CancelReaderDoesNotTreatUnterminatedFrameOrEofAsCancellation()
    {
        await using var invocation = new SkillInvocation { ResponseId = "req-active" };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"schema_version\":\"1.0\",\"type\":\"cancel\",\"request_id\":\"req-active\"}"));
        invocation.ListenForCancellation(input);
        Assert.IsFalse(invocation.HostCancelled);
        Assert.IsFalse(invocation.CancellationToken.IsCancellationRequested);
    }
}
