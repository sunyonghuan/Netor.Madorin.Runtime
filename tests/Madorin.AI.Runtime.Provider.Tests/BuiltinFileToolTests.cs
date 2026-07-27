using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class BuiltinFileToolTests
{
    private string _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Join(Path.GetTempPath(), $"madorin-builtin-fs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileTools_AuthorizedOperations_CompleteEndToEnd()
    {
        var executor = new BuiltinFsToolExecutor();
        var permission = CreatePermission(allowOverwrite: true, allowMove: true, allowDelete: true);

        var mkdir = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.DirectoryCreateToolId,
            """{"path":"docs"}""",
            permission);
        Assert.IsTrue(mkdir.Success, mkdir.Error);

        var write = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileWriteToolId,
            """{"path":"docs/data.txt","content":"one\ntwo\nthree\n","encoding":"utf-8","mode":"create"}""",
            permission);
        Assert.IsTrue(write.Success, write.Error);

        var status = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileStatusToolId,
            """{"path":"docs/data.txt","encoding":"utf-8"}""",
            permission);
        Assert.IsTrue(status.Success, status.Error);
        using (var output = JsonDocument.Parse(status.OutputJson!))
        {
            Assert.IsTrue(output.RootElement.GetProperty("exists").GetBoolean());
            Assert.AreEqual("file", output.RootElement.GetProperty("kind").GetString());
        }

        var read = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileReadToolId,
            """{"path":"docs/data.txt","encoding":"utf-8"}""",
            permission);
        Assert.IsTrue(read.Success, read.Error);
        using (var output = JsonDocument.Parse(read.OutputJson!))
        {
            Assert.AreEqual(
                "1| one\n2| two\n3| three\n4| ",
                output.RootElement.GetProperty("content").GetString());
        }

        var search = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileSearchToolId,
            """{"path":"docs","query":"two","encoding":"utf-8","maxResults":5}""",
            permission);
        Assert.IsTrue(search.Success, search.Error);
        using (var output = JsonDocument.Parse(search.OutputJson!))
        {
            var match = Assert.ContainsSingle(
                output.RootElement.GetProperty("matches").EnumerateArray().ToArray());
            Assert.AreEqual(2, match.GetProperty("line").GetInt32());
            Assert.AreEqual(1, match.GetProperty("column").GetInt32());
        }

        var currentBytes = await File.ReadAllBytesAsync(
            Path.Join(_root, "docs", "data.txt"),
            TestContext.CancellationToken);
        var expectedHash = Convert.ToHexString(SHA256.HashData(currentBytes));
        var patchArguments = $$"""
            {"path":"docs/data.txt","encoding":"utf-8","expectedHash":"{{expectedHash}}","operations":[{"type":"replace","startLine":2,"endLine":2,"content":"second"}]}
            """;
        var patched = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileEditToolId,
            patchArguments,
            permission);
        Assert.IsTrue(patched.Success, patched.Error);
        Assert.AreEqual(
            "one\nsecond\nthree\n",
            await File.ReadAllTextAsync(
                Path.Join(_root, "docs", "data.txt"),
                TestContext.CancellationToken));

        var copy = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileCopyToolId,
            """{"source":"docs/data.txt","destination":"docs/copy.txt"}""",
            permission);
        Assert.IsTrue(copy.Success, copy.Error);

        var move = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileMoveToolId,
            """{"source":"docs/copy.txt","destination":"docs/moved.txt"}""",
            permission);
        Assert.IsTrue(move.Success, move.Error);
        Assert.IsTrue(File.Exists(Path.Join(_root, "docs", "moved.txt")));

        var delete = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileDeleteToolId,
            """{"path":"docs/moved.txt"}""",
            permission);
        Assert.IsTrue(delete.Success, delete.Error);
        Assert.IsFalse(File.Exists(Path.Join(_root, "docs", "moved.txt")));
    }

    [TestMethod]
    [DataRow("""{"path":"../escape.txt","encoding":"utf-8"}""")]
    [DataRow("""{"path":"//?/C:/Windows/win.ini","encoding":"utf-8"}""")]
    [DataRow("""{"path":"//server/share/file.txt","encoding":"utf-8"}""")]
    [DataRow("""{"path":"file.txt:secret","encoding":"utf-8"}""")]
    [DataRow("""{"path":".madorin/secret.txt","encoding":"utf-8"}""")]
    public async Task FileRead_DangerousPath_IsRejected(string argumentsJson)
    {
        var result = await ExecuteAsync(
            new BuiltinFsToolExecutor(),
            BuiltinToolRegistry.FileReadToolId,
            argumentsJson,
            CreatePermission());

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task FileRead_SymbolicLink_IsRejected()
    {
        var target = Path.Join(_root, "target.txt");
        var link = Path.Join(_root, "link.txt");
        await File.WriteAllTextAsync(target, "secret", TestContext.CancellationToken);
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable in this environment: {ex.Message}");
        }

        var result = await ExecuteAsync(
            new BuiltinFsToolExecutor(),
            BuiltinToolRegistry.FileReadToolId,
            """{"path":"link.txt","encoding":"utf-8"}""",
            CreatePermission());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "links");
    }

    [TestMethod]
    public async Task FileRead_WindowsPathCaseDifference_IsAllowed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This assertion covers Windows path case behavior.");
        }

        await File.WriteAllTextAsync(
            Path.Join(_root, "CaseSensitiveName.txt"),
            "content",
            TestContext.CancellationToken);

        var result = await ExecuteAsync(
            new BuiltinFsToolExecutor(),
            BuiltinToolRegistry.FileReadToolId,
            """{"path":"casesensitivename.TXT","encoding":"utf-8"}""",
            CreatePermission());

        Assert.IsTrue(result.Success, result.Error);
    }

    [TestMethod]
    public async Task FileWrite_ExistingFileWithoutOverwriteGrant_IsRejected()
    {
        await File.WriteAllTextAsync(
            Path.Join(_root, "existing.txt"),
            "original",
            TestContext.CancellationToken);

        var result = await ExecuteAsync(
            new BuiltinFsToolExecutor(),
            BuiltinToolRegistry.FileWriteToolId,
            """{"path":"existing.txt","content":"replacement","encoding":"utf-8","mode":"overwrite","expectedHash":"0000000000000000000000000000000000000000000000000000000000000000"}""",
            CreatePermission());

        Assert.IsFalse(result.Success);
        Assert.AreEqual(
            "original",
            await File.ReadAllTextAsync(
                Path.Join(_root, "existing.txt"),
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task FileRead_ExplicitEncodingSupportsGb2312AndLineRange()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Join(_root, "gb2312.txt");
        await File.WriteAllBytesAsync(
            path,
            Encoding.GetEncoding(936).GetBytes("第一行\r\n第二行"),
            TestContext.CancellationToken);

        var result = await ExecuteAsync(
            new BuiltinFsToolExecutor(),
            BuiltinToolRegistry.FileReadToolId,
            """{"path":"gb2312.txt","encoding":"gb2312","startLine":2,"endLine":2}""",
            CreatePermission());

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual("2| 第二行", output.RootElement.GetProperty("content").GetString());
        Assert.AreEqual(936, output.RootElement.GetProperty("codePage").GetInt32());
        Assert.AreEqual(2, output.RootElement.GetProperty("totalLines").GetInt32());
    }

    [TestMethod]
    public async Task FileAppend_RequiresExpectedHashAndPreservesEncoding()
    {
        var path = Path.Join(_root, "append.txt");
        await File.WriteAllTextAsync(
            path,
            "one\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.CancellationToken);
        var hash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.CancellationToken)));

        var result = await ExecuteAsync(
            new BuiltinFsToolExecutor(),
            BuiltinToolRegistry.FileAppendToolId,
            $$"""{"path":"append.txt","content":"two\n","encoding":"utf-8","expectedHash":"{{hash}}"}""",
            CreatePermission(allowOverwrite: true));

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            "one\ntwo\n",
            await File.ReadAllTextAsync(path, TestContext.CancellationToken));
    }

    private Task<ToolResult> ExecuteAsync(
        BuiltinFsToolExecutor executor,
        string toolId,
        string argumentsJson,
        ToolPermissionContext permission) => executor.ExecuteAsync(
        new ToolInvocation(
            Guid.NewGuid().ToString("N"),
            toolId,
            "agent-1",
            ParentAgentId: null,
            argumentsJson,
            "run-1",
            "session-1",
            PermissionContext: permission),
        TestContext.CancellationToken);

    private ToolPermissionContext CreatePermission(
        bool allowOverwrite = false,
        bool allowMove = false,
        bool allowDelete = false) => new(
        "grant-1",
        "run-1",
        _root,
        [_root],
        [_root],
        allowOverwrite,
        allowMove,
        allowDelete,
        AllowedExecutables: []);
}
