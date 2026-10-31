using System.Security.Cryptography;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Core.Concurrency;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Server;
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
    [DataRow("png", "image/png")]
    [DataRow("jpeg", "image/jpeg")]
    [DataRow("pdf", "application/pdf")]
    [DataRow("unknown", "application/octet-stream")]
    public async Task AttachFile_StoresBytesAndBoundedMetadata(string format, string expectedType)
    {
        byte[] bytes = format switch
        {
            "png" => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC1lEQVR42mP8/x8AAwMCAO+jP1kAAAAASUVORK5CYII="),
            "jpeg" => [255, 216, 255, 224, 0, 16],
            "pdf" => "%PDF-1.7\n"u8.ToArray(),
            _ => RandomNumberGenerator.GetBytes(2 * 1024 * 1024)
        };
        var path = Path.Join(_root, "attachment.png");
        await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken);
        await using var store = new BlobStagingStore(Path.Join(_root, "store"), new RuntimeLimits(), 16 * 1024 * 1024);
        var executor = new BuiltinFsToolExecutor(blobStore: store);
        var result = await ExecuteAsync(executor, BuiltinToolRegistry.FileAttachToolId,
            """{"path":"attachment.png"}""", CreatePermission());
        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.ResultBlob);
        Assert.AreEqual(expectedType, result.ResultBlob.ContentType);
        Assert.IsNull(result.ResultHash);
        Assert.IsLessThan(512, result.OutputJson!.Length);
        Assert.DoesNotContain(_root, result.OutputJson, StringComparison.OrdinalIgnoreCase);
        using var metadata = JsonDocument.Parse(result.OutputJson);
        Assert.AreEqual("attachment.png", metadata.RootElement.GetProperty("fileName").GetString());
        Assert.AreEqual(bytes.LongLength, metadata.RootElement.GetProperty("byteLength").GetInt64());
        Assert.AreEqual(result.ResultBlob.Sha256, metadata.RootElement.GetProperty("sha256").GetString());
        await using var read = await store.OpenReadAsync(result.ResultBlob, TestContext.CancellationToken);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)),
            Convert.ToHexString(await SHA256.HashDataAsync(read, TestContext.CancellationToken)));
    }

    [TestMethod]
    public async Task AttachFile_RejectsWrongHashWithoutPublishingBlob()
    {
        await File.WriteAllBytesAsync(Path.Join(_root, "data.bin"), [1, 2, 3], TestContext.CancellationToken);
        var storage = Path.Join(_root, "store");
        await using var store = new BlobStagingStore(storage, new RuntimeLimits(), 16 * 1024 * 1024);
        var executor = new BuiltinFsToolExecutor(blobStore: store);
        var result = await ExecuteAsync(executor, BuiltinToolRegistry.FileAttachToolId,
            $$"""{"path":"data.bin","expectedHash":"{{new string('0', 64)}}"}""", CreatePermission());
        Assert.IsFalse(result.Success);
        Assert.IsNull(result.ResultBlob);
        Assert.IsEmpty(Directory.GetFiles(Path.Join(storage, "staging")));
        Assert.IsEmpty(Directory.GetFiles(Path.Join(storage, "blobs")));
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
    public async Task FileSearch_RanksMatchesAndPagesThem()
    {
        var permission = CreatePermission();
        await File.WriteAllTextAsync(
            Path.Join(_root, "once.txt"),
            "alpha\n",
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(_root, "often.txt"),
            "alpha alpha\nalpha\n",
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(_root, "none.txt"),
            "beta\n",
            TestContext.CancellationToken);

        var executor = new BuiltinFsToolExecutor();
        var first = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileSearchToolId,
            """{"path":".","query":"alpha","encoding":"utf-8","page":1,"pageSize":1}""",
            permission);
        Assert.IsTrue(first.Success, first.Error);
        using (var output = JsonDocument.Parse(first.OutputJson!))
        {
            Assert.AreEqual(2, output.RootElement.GetProperty("total").GetInt32());
            Assert.AreEqual(1, output.RootElement.GetProperty("page").GetInt32());
            Assert.AreEqual(1, output.RootElement.GetProperty("pageSize").GetInt32());
            Assert.IsTrue(output.RootElement.GetProperty("hasMore").GetBoolean());
            var match = Assert.ContainsSingle(
                output.RootElement.GetProperty("matches").EnumerateArray().ToArray());
            Assert.AreEqual("often.txt", match.GetProperty("path").GetString());
            Assert.AreEqual(3, match.GetProperty("hits").GetInt32());
        }

        var second = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileSearchToolId,
            """{"path":".","query":"alpha","encoding":"utf-8","page":2,"pageSize":1}""",
            permission);
        Assert.IsTrue(second.Success, second.Error);
        using (var output = JsonDocument.Parse(second.OutputJson!))
        {
            Assert.IsFalse(output.RootElement.GetProperty("hasMore").GetBoolean());
            var match = Assert.ContainsSingle(
                output.RootElement.GetProperty("matches").EnumerateArray().ToArray());
            Assert.AreEqual("once.txt", match.GetProperty("path").GetString());
        }
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

    [TestMethod]
    public async Task FileEdit_TwoExecutorsWithSameHash_OnlyOneSucceeds()
    {
        var path = Path.Join(_root, "edit.txt");
        await File.WriteAllTextAsync(path, "before\n", TestContext.CancellationToken);
        var hash = await ComputeHashAsync(path);
        var workspaceMutationLock = new WorkspaceMutationLock();
        var first = new BuiltinFsToolExecutor(workspaceMutationLock);
        var second = new BuiltinFsToolExecutor(workspaceMutationLock);
        var permission = CreatePermission(allowOverwrite: true);
        var firstArguments = CreateEditArguments(hash, "first");
        var secondArguments = CreateEditArguments(hash, "second");

        var results = await Task.WhenAll(
            ExecuteAsync(first, BuiltinToolRegistry.FileEditToolId, firstArguments, permission),
            ExecuteAsync(second, BuiltinToolRegistry.FileEditToolId, secondArguments, permission));

        Assert.AreEqual(1, results.Count(static result => result.Success));
        var conflict = Assert.ContainsSingle(results.Where(static result => !result.Success));
        StringAssert.Contains(conflict.Error, "expected SHA-256");
        var content = await File.ReadAllTextAsync(path, TestContext.CancellationToken);
        Assert.IsTrue(content is "first\n" or "second\n");
    }

    [TestMethod]
    public async Task FileAppend_TwoExecutorsWithSameHash_DoesNotSilentlyOverwrite()
    {
        var path = Path.Join(_root, "append-concurrent.txt");
        await File.WriteAllTextAsync(path, "start\n", TestContext.CancellationToken);
        var hash = await ComputeHashAsync(path);
        var workspaceMutationLock = new WorkspaceMutationLock();
        var first = new BuiltinFsToolExecutor(workspaceMutationLock);
        var second = new BuiltinFsToolExecutor(workspaceMutationLock);
        var permission = CreatePermission(allowOverwrite: true);

        var results = await Task.WhenAll(
            ExecuteAsync(
                first,
                BuiltinToolRegistry.FileAppendToolId,
                CreateAppendArguments(hash, "first\n"),
                permission),
            ExecuteAsync(
                second,
                BuiltinToolRegistry.FileAppendToolId,
                CreateAppendArguments(hash, "second\n"),
                permission));

        Assert.AreEqual(1, results.Count(static result => result.Success));
        Assert.AreEqual(1, results.Count(static result => !result.Success));
        var content = await File.ReadAllTextAsync(path, TestContext.CancellationToken);
        Assert.IsTrue(content is "start\nfirst\n" or "start\nsecond\n");
    }

    [TestMethod]
    public async Task DirectoryDelete_ConcurrentChildEdit_ObservesSerializedState()
    {
        var directory = Path.Join(_root, "tree");
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "child.txt");
        await File.WriteAllTextAsync(path, "before\n", TestContext.CancellationToken);
        var hash = await ComputeHashAsync(path);
        var workspaceMutationLock = new WorkspaceMutationLock();
        var deleteExecutor = new BuiltinFsToolExecutor(workspaceMutationLock);
        var editExecutor = new BuiltinFsToolExecutor(workspaceMutationLock);
        var permission = CreatePermission(
            allowOverwrite: true,
            allowDelete: true);

        var results = await Task.WhenAll(
            ExecuteAsync(
                deleteExecutor,
                BuiltinToolRegistry.FileDeleteToolId,
                """{"path":"tree","recursive":true}""",
                permission),
            ExecuteAsync(
                editExecutor,
                BuiltinToolRegistry.FileEditToolId,
                CreateEditArguments(hash, "after", "tree/child.txt"),
                permission));

        Assert.IsTrue(results[0].Success, results[0].Error);
        Assert.IsTrue(results[1].Success
            || results[1].Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) is true,
            results[1].Error);
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public async Task MutationLock_CancelledWaiter_DoesNotBlockFollowingWrite()
    {
        var workspaceMutationLock = new WorkspaceMutationLock();
        var executor = new BuiltinFsToolExecutor(workspaceMutationLock);
        var permission = CreatePermission();
        await using var heldLease = await workspaceMutationLock.AcquireAsync(
            TestContext.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var cancelledWrite = ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileWriteToolId,
            """{"path":"cancelled.txt","content":"cancelled","encoding":"utf-8","mode":"create"}""",
            permission,
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await cancelledWrite);
        await heldLease.DisposeAsync();

        var result = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileWriteToolId,
            """{"path":"following.txt","content":"written","encoding":"utf-8","mode":"create"}""",
            permission);
        Assert.IsTrue(result.Success, result.Error);
    }

    [TestMethod]
    public async Task MutationLock_FailedMutation_ReleasesLockForFollowingWrite()
    {
        var path = Path.Join(_root, "failure.txt");
        await File.WriteAllTextAsync(path, "before\n", TestContext.CancellationToken);
        var workspaceMutationLock = new WorkspaceMutationLock();
        var executor = new BuiltinFsToolExecutor(workspaceMutationLock);
        var permission = CreatePermission(allowOverwrite: true);
        var failure = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileAppendToolId,
            CreateAppendArguments(new string('0', 64), "lost\n", "failure.txt"),
            permission);
        Assert.IsFalse(failure.Success);

        var hash = await ComputeHashAsync(path);
        var success = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileAppendToolId,
            CreateAppendArguments(hash, "after\n", "failure.txt"),
            permission);

        Assert.IsTrue(success.Success, success.Error);
        Assert.AreEqual(
            "before\nafter\n",
            await File.ReadAllTextAsync(path, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task FileInspectAndMergedWrite_CoverListStatAppendAndMkdir()
    {
        var executor = new BuiltinFsToolExecutor();
        var permission = CreatePermission(allowOverwrite: true);

        var mkdir = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileWriteToolId,
            """{"path":"docs","mode":"mkdir"}""",
            permission);
        Assert.IsTrue(mkdir.Success, mkdir.Error);
        using (var output = JsonDocument.Parse(mkdir.OutputJson!))
        {
            Assert.IsTrue(output.RootElement.GetProperty("written").GetBoolean());
            Assert.AreEqual("mkdir", output.RootElement.GetProperty("mode").GetString());
        }

        var write = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileWriteToolId,
            """{"path":"docs/data.txt","content":"one\n","encoding":"utf-8","mode":"create"}""",
            permission);
        Assert.IsTrue(write.Success, write.Error);

        var hash = await ComputeHashAsync(Path.Join(_root, "docs", "data.txt"));
        var append = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileWriteToolId,
            $$"""{"path":"docs/data.txt","mode":"append","content":"two\n","encoding":"utf-8","expectedHash":"{{hash}}"}""",
            permission);
        Assert.IsTrue(append.Success, append.Error);
        using (var output = JsonDocument.Parse(append.OutputJson!))
        {
            Assert.AreEqual("append", output.RootElement.GetProperty("mode").GetString());
        }

        var list = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileInspectToolId,
            """{"mode":"list","path":"docs"}""",
            permission);
        Assert.IsTrue(list.Success, list.Error);
        using (var output = JsonDocument.Parse(list.OutputJson!))
        {
            Assert.IsTrue(output.RootElement.TryGetProperty("entries", out _));
        }

        var status = await ExecuteAsync(
            executor,
            BuiltinToolRegistry.FileInspectToolId,
            """{"mode":"stat","path":"docs/data.txt"}""",
            permission);
        Assert.IsTrue(status.Success, status.Error);
        using (var output = JsonDocument.Parse(status.OutputJson!))
        {
            Assert.AreEqual("file", output.RootElement.GetProperty("kind").GetString());
        }
    }

    [TestMethod]
    public async Task FileWrite_OverwriteMissingFile_CreatesFile()
    {
        var result = await ExecuteAsync(
            new BuiltinFsToolExecutor(),
            BuiltinToolRegistry.FileWriteToolId,
            """{"path":"missing.txt","mode":"overwrite","content":"created","encoding":"utf-8"}""",
            CreatePermission(allowOverwrite: false));

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            "created",
            await File.ReadAllTextAsync(Path.Join(_root, "missing.txt"), TestContext.CancellationToken));
    }

    private Task<ToolResult> ExecuteAsync(
        BuiltinFsToolExecutor executor,
        string toolId,
        string argumentsJson,
        ToolPermissionContext permission,
        CancellationToken? ct = null) => executor.ExecuteAsync(
        new ToolInvocation(
            Guid.NewGuid().ToString("N"),
            toolId,
            "agent-1",
            ParentAgentId: null,
            argumentsJson,
            "run-1",
            "session-1",
            PermissionContext: permission),
        ct ?? TestContext.CancellationToken);

    private async Task<string> ComputeHashAsync(string path) => Convert.ToHexString(
        SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.CancellationToken)));

    private static string CreateEditArguments(
        string hash,
        string content,
        string path = "edit.txt") => WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("path", path);
            writer.WriteString("encoding", "utf-8");
            writer.WriteString("expectedHash", hash);
            writer.WriteStartArray("operations");
            writer.WriteStartObject();
            writer.WriteString("type", "replace");
            writer.WriteNumber("startLine", 1);
            writer.WriteNumber("endLine", 1);
            writer.WriteString("content", content);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string CreateAppendArguments(
        string hash,
        string content,
        string path = "append-concurrent.txt") =>
        WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("path", path);
            writer.WriteString("content", content);
            writer.WriteString("encoding", "utf-8");
            writer.WriteString("expectedHash", hash);
            writer.WriteEndObject();
        });

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        write(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

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
