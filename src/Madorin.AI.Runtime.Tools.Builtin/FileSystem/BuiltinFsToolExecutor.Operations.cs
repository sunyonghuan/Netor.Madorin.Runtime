using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Enumeration;
using Microsoft.Win32.SafeHandles;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

public sealed partial class BuiltinFsToolExecutor
{
    private const int MaxSearchFiles = 10_000;
    private const int MaxSearchResults = 100;
    private const long MaxTransferFileSizeBytes = 64L * 1024 * 1024;

    private FsPreparedExecution PrepareCore(ToolInvocation invocation)
    {
        if (!CanExecute(invocation.ToolId))
        {
            throw new InvalidOperationException("The file executor cannot execute this tool.");
        }

        var permission = invocation.PermissionContext
            ?? throw new UnauthorizedAccessException("A tool permission grant is required.");
        if (!string.Equals(permission.RunId, invocation.RunId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The tool permission grant belongs to another Run.");
        }

        using var document = JsonDocument.Parse(invocation.ArgumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Tool arguments must be a JSON object.");
        }

        var arguments = document.RootElement.Clone();
        var policy = new BuiltinFsPathPolicy(permission);
        return invocation.ToolId switch
        {
            BuiltinToolRegistry.FileListToolId => PrepareList(invocation, arguments, policy),
            BuiltinToolRegistry.FileStatusToolId => PrepareStatus(invocation, arguments, policy),
            BuiltinToolRegistry.FileReadToolId => PrepareRead(invocation, arguments, policy),
            BuiltinToolRegistry.FileWriteToolId =>
                PrepareWrite(invocation, arguments, permission, policy),
            BuiltinToolRegistry.FileAppendToolId =>
                PrepareAppend(invocation, arguments, permission, policy),
            BuiltinToolRegistry.DirectoryCreateToolId =>
                PrepareDirectoryCreate(invocation, arguments, policy),
            BuiltinToolRegistry.FileCopyToolId =>
                PrepareCopy(invocation, arguments, permission, policy),
            BuiltinToolRegistry.FileMoveToolId =>
                PrepareMove(invocation, arguments, permission, policy),
            BuiltinToolRegistry.FileDeleteToolId =>
                PrepareDelete(invocation, arguments, permission, policy),
            BuiltinToolRegistry.FileSearchToolId => PrepareSearch(invocation, arguments, policy),
            BuiltinToolRegistry.FileEditToolId =>
                PrepareEdit(invocation, arguments, permission, policy),
            _ => throw new InvalidOperationException("The file tool is not implemented.")
        };
    }

    private static FsPreparedExecution PrepareList(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy)
    {
        var directory = policy.PinReadDirectory(GetOptionalString(arguments, "path") ?? ".");
        return new FsPreparedExecution(
            policy.GetTargetSummary(directory.Path),
            ct => Task.FromResult(List(invocation, arguments, directory, policy, ct)),
            [directory]);
    }

    private static FsPreparedExecution PrepareStatus(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy)
    {
        var requestedPath = GetRequiredString(arguments, "path");
        var path = policy.ResolveReadPath(requestedPath);
        PinnedPath? target = File.Exists(path)
            ? policy.PinReadFile(requestedPath)
            : Directory.Exists(path)
                ? policy.PinReadDirectory(requestedPath)
                : null;
        return new FsPreparedExecution(
            policy.GetTargetSummary(path),
            ct => StatusAsync(invocation, path, target, ct),
            target is null ? [] : [target]);
    }

    private static FsPreparedExecution PrepareRead(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy)
    {
        var file = policy.PinReadFile(GetRequiredString(arguments, "path"));
        return new FsPreparedExecution(
            policy.GetTargetSummary(file.Path),
            ct => ReadAsync(invocation, arguments, file, ct),
            [file]);
    }

    private static FsPreparedExecution PrepareWrite(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy)
    {
        var path = GetRequiredString(arguments, "path");
        var mode = GetRequiredString(arguments, "mode");
        if (mode is not ("create" or "overwrite"))
        {
            throw new ArgumentException("Tool argument 'mode' must be create or overwrite.");
        }

        var resolvedPath = policy.ResolveWritePath(path);
        var exists = File.Exists(resolvedPath) || Directory.Exists(resolvedPath);
        if (string.Equals(mode, "create", StringComparison.Ordinal) && exists)
        {
            throw new IOException("The write target already exists.");
        }

        if (string.Equals(mode, "overwrite", StringComparison.Ordinal))
        {
            if (!permission.AllowOverwrite)
            {
                throw new ToolPermissionRequiredException(
                    "Overwriting this file is not permitted.");
            }

            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException("The overwrite target was not found.", path);
            }
        }

        PinnedPath? existing = null;
        try
        {
            existing = string.Equals(mode, "overwrite", StringComparison.Ordinal)
                ? policy.PinReadFile(path, allowDeleteSharing: true)
                : null;
            var parent = policy.PinWriteParent(path);
            return new FsPreparedExecution(
                policy.GetTargetSummary(parent.Path),
                ct => WriteAsync(invocation, arguments, policy, existing, parent, ct),
                existing is null ? [parent] : [existing, parent]);
        }
        catch
        {
            existing?.Dispose();
            throw;
        }
    }

    private static FsPreparedExecution PrepareAppend(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy)
    {
        if (!permission.AllowOverwrite)
        {
            throw new ToolPermissionRequiredException(
                "Appending to an existing file requires overwrite permission.");
        }

        var file = policy.PinReadFile(
            GetRequiredString(arguments, "path"),
            allowDeleteSharing: true);
        try
        {
            var parent = policy.PinWriteParent(file.Path);
            return new FsPreparedExecution(
                policy.GetTargetSummary(file.Path),
                ct => AppendAsync(invocation, arguments, policy, file, parent, ct),
                [file, parent]);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static FsPreparedExecution PrepareDirectoryCreate(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy)
    {
        var path = GetRequiredString(arguments, "path");
        var createParents = GetOptionalBoolean(arguments, "createParents") ?? false;
        var parent = createParents
            ? policy.PinWriteAncestor(path)
            : policy.PinWriteParent(path);
        return new FsPreparedExecution(
            policy.GetTargetSummary(parent.Path),
            ct => Task.FromResult(CreateDirectory(invocation, parent, policy, ct)),
            [parent]);
    }

    private static FsPreparedExecution PrepareCopy(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy)
    {
        var sourcePath = GetRequiredString(arguments, "source");
        var resolvedSource = policy.ResolveReadPath(sourcePath);
        var source = File.Exists(resolvedSource)
            ? policy.PinReadFile(sourcePath)
            : Directory.Exists(resolvedSource)
                ? policy.PinReadDirectory(sourcePath)
                : throw new FileNotFoundException("The copy source was not found.", sourcePath);
        try
        {
            var destination = policy.PinWriteParent(GetRequiredString(arguments, "destination"));
            if ((File.Exists(destination.Path) || Directory.Exists(destination.Path))
                && !permission.AllowOverwrite)
            {
                destination.Dispose();
                throw new ToolPermissionRequiredException(
                    "Overwriting the copy destination is not permitted.");
            }

            return new FsPreparedExecution(
                $"{policy.GetTargetSummary(source.Path)}->{policy.GetTargetSummary(destination.Path)}",
                ct => CopyAsync(
                    invocation,
                    arguments,
                    permission,
                    policy,
                    source,
                    destination,
                    ct),
                [source, destination]);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static FsPreparedExecution PrepareMove(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy)
    {
        if (!permission.AllowMove)
        {
            throw new UnauthorizedAccessException("Moving paths is not permitted.");
        }

        var source = policy.PinWriteParent(GetRequiredString(arguments, "source"));
        if (!File.Exists(source.Path) && !Directory.Exists(source.Path))
        {
            source.Dispose();
            throw new FileNotFoundException("The move source was not found.", source.Path);
        }

        try
        {
            var destination = policy.PinWriteParent(GetRequiredString(arguments, "destination"));
            return new FsPreparedExecution(
                $"{policy.GetTargetSummary(source.Path)}->{policy.GetTargetSummary(destination.Path)}",
                ct => Task.FromResult(Move(invocation, permission, policy, source, destination, ct)),
                [source, destination]);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static FsPreparedExecution PrepareDelete(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy)
    {
        if (!permission.AllowDelete)
        {
            throw new UnauthorizedAccessException("Deleting paths is not permitted.");
        }

        var parent = policy.PinWriteParent(GetRequiredString(arguments, "path"));
        return new FsPreparedExecution(
            policy.GetTargetSummary(parent.Path),
            ct => Task.FromResult(Delete(invocation, arguments, policy, parent, ct)),
            [parent]);
    }

    private static FsPreparedExecution PrepareSearch(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy)
    {
        var directory = policy.PinReadDirectory(GetRequiredString(arguments, "path"));
        return new FsPreparedExecution(
            policy.GetTargetSummary(directory.Path),
            ct => SearchAsync(invocation, arguments, directory, policy, ct),
            [directory]);
    }

    private static FsPreparedExecution PrepareEdit(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy)
    {
        if (!permission.AllowOverwrite)
        {
            throw new ToolPermissionRequiredException(
                "Editing an existing file requires overwrite permission.");
        }

        var file = policy.PinReadFile(
            GetRequiredString(arguments, "path"),
            allowDeleteSharing: true);
        try
        {
            var parent = policy.PinWriteParent(file.Path);
            return new FsPreparedExecution(
                policy.GetTargetSummary(file.Path),
                ct => EditAsync(invocation, arguments, policy, file, parent, ct),
                [file, parent]);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static ToolResult List(
        ToolInvocation invocation,
        JsonElement arguments,
        PinnedPath directory,
        BuiltinFsPathPolicy policy,
        CancellationToken ct)
    {
        var recursive = GetOptionalBoolean(arguments, "recursive") ?? false;
        var maxDepth = GetOptionalInt32(arguments, "maxDepth") ?? (recursive ? 8 : 0);
        var limit = GetOptionalInt32(arguments, "limit") ?? 1000;
        if (maxDepth is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), "maxDepth must be between 0 and 32.");
        }

        if (limit is < 1 or > MaxListEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                $"limit must be between 1 and {MaxListEntries}.");
        }

        var pattern = GetOptionalString(arguments, "pattern") ?? "*";
        if (pattern.Length is < 1 or > 256)
        {
            throw new ArgumentException("Tool argument 'pattern' must contain 1 to 256 characters.");
        }

        var entries = new List<FileSystemInfo>();
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            MaxRecursionDepth = maxDepth,
            RecurseSubdirectories = recursive,
            ReturnSpecialDirectories = false
        };
        var truncated = false;
        foreach (var entry in new DirectoryInfo(directory.Path)
                     .EnumerateFileSystemInfos("*", options)
                     .OrderBy(item => item.FullName, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(directory.Path, entry.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!FileSystemName.MatchesSimpleExpression(
                    pattern,
                    relative,
                    ignoreCase: OperatingSystem.IsWindows()))
            {
                continue;
            }

            try
            {
                _ = policy.ResolveReadPath(entry.FullName);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (entries.Count == limit)
            {
                truncated = true;
                break;
            }

            entries.Add(entry);
        }

        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("entries");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", entry.Name);
                writer.WriteString(
                    "path",
                    Path.GetRelativePath(directory.Path, entry.FullName)
                        .Replace(Path.DirectorySeparatorChar, '/'));
                writer.WriteString("kind", entry is DirectoryInfo ? "directory" : "file");
                if (entry is FileInfo file)
                {
                    writer.WriteNumber("byteLength", file.Length);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("truncated", truncated);
            writer.WriteEndObject();
        });
        EnsureResultSize(output, "Directory listing");
        return Success(invocation, output);
    }

    private static async Task<ToolResult> StatusAsync(
        ToolInvocation invocation,
        string path,
        PinnedPath? target,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var exists = target is not null;
        var isDirectory = exists && Directory.Exists(path);
        long? byteLength = exists && !isDirectory
            ? RandomAccess.GetLength(target!.Handle)
            : null;
        var lastWriteTime = exists
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
            : (DateTimeOffset?)null;
        string? sha256 = null;
        string? contentKind = null;
        if (byteLength is { } length && length <= MaxTransferFileSizeBytes)
        {
            var bytes = await ReadAllAsync(target!.Handle, checked((int)MaxTransferFileSizeBytes), ct)
                .ConfigureAwait(false);
            sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            contentKind = DetectContentKind(bytes);
        }
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("exists", exists);
            if (exists)
            {
                writer.WriteString("kind", isDirectory ? "directory" : "file");
            }
            else
            {
                writer.WriteNull("kind");
            }

            if (byteLength is { } length)
            {
                writer.WriteNumber("byteLength", length);
            }
            else
            {
                writer.WriteNull("byteLength");
            }

            if (lastWriteTime is { } timestamp)
            {
                writer.WriteString("lastWriteTime", timestamp);
            }
            else
            {
                writer.WriteNull("lastWriteTime");
            }

            if (sha256 is not null)
            {
                writer.WriteString("sha256", sha256);
                writer.WriteString("contentKind", contentKind);
            }
            else
            {
                writer.WriteNull("sha256");
                writer.WriteNull("contentKind");
            }

            writer.WriteEndObject();
        });
        return Success(invocation, output);
    }

    private static async Task<ToolResult> ReadAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        PinnedPath file,
        CancellationToken ct)
    {
        var bytes = await ReadAllAsync(file.Handle, MaxFileSizeBytes, ct).ConfigureAwait(false);
        var decoded = FileSystemTextEncoding.Decode(
            bytes,
            GetRequiredString(arguments, "encoding"));
        var lines = GetLineSpans(decoded.Content);
        var startLine = GetOptionalInt32(arguments, "startLine") ?? (lines.Count == 0 ? 0 : 1);
        var requestedEndLine = GetOptionalInt32(arguments, "endLine");
        if (lines.Count == 0)
        {
            if (startLine is not (0 or 1) || requestedEndLine is > 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(arguments),
                    "The file is empty.");
            }

            startLine = 0;
        }
        else if (startLine < 1 || startLine > lines.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                $"Start line must be between 1 and {lines.Count}.");
        }

        var endLine = lines.Count == 0
            ? 0
            : requestedEndLine ?? Math.Min(lines.Count, startLine + 199);
        if (endLine < startLine || endLine > lines.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                $"End line must be between {startLine} and {lines.Count}.");
        }

        var numberedContent = lines.Count == 0
            ? string.Empty
            : string.Join(
                '\n',
                Enumerable.Range(startLine, endLine - startLine + 1)
                    .Select(lineNumber =>
                    {
                        var span = lines[lineNumber - 1];
                        return $"{lineNumber}| {decoded.Content[span.Start..span.ContentEnd]}";
                    }));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("content", numberedContent);
            writer.WriteString("encoding", decoded.Encoding);
            writer.WriteNumber("codePage", decoded.CodePage);
            writer.WriteNumber("byteLength", bytes.Length);
            writer.WriteNumber("startLine", startLine);
            writer.WriteNumber("endLine", endLine);
            writer.WriteNumber("totalLines", lines.Count);
            writer.WriteString("sha256", hash);
            writer.WriteBoolean("hasBom", decoded.HasBom);
            writer.WriteString("lineEnding", decoded.LineEnding);
            writer.WriteBoolean("endsWithNewline", decoded.EndsWithNewline);
            writer.WriteBoolean("truncated", endLine < lines.Count);
            writer.WriteEndObject();
        });
        EnsureResultSize(output, "File read result");
        return Success(invocation, output);
    }

    private static async Task<ToolResult> WriteAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy,
        PinnedPath? existing,
        PinnedPath parent,
        CancellationToken ct)
    {
        var encodingName = GetRequiredString(arguments, "encoding");
        var content = GetRequiredString(arguments, "content");
        var includeBom = false;
        var existingLineEnding = "lf";
        if (existing is not null)
        {
            var currentBytes = await ReadAllAsync(existing.Handle, MaxFileSizeBytes, ct)
                .ConfigureAwait(false);
            ValidateExpectedHash(arguments, currentBytes);
            var current = FileSystemTextEncoding.Decode(currentBytes, encodingName);
            includeBom = ResolveBom(arguments, current.HasBom, isExisting: true);
            existingLineEnding = current.LineEnding;
            existing.Dispose();
        }
        else
        {
            includeBom = ResolveBom(arguments, existingHasBom: false, isExisting: false);
        }

        content = FileSystemTextEncoding.NormalizeLineEndings(
            content,
            GetOptionalString(arguments, "lineEnding") ?? "preserve",
            existingLineEnding);
        var bytes = FileSystemTextEncoding.Encode(content, encodingName, includeBom);
        if (bytes.Length > MaxFileSizeBytes)
        {
            throw new InvalidDataException("The content exceeds the 1 MB write limit.");
        }

        await AtomicWriteAsync(
            parent,
            policy,
            bytes,
            overwrite: existing is not null,
            ct).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("written", true);
            writer.WriteNumber("byteLength", bytes.Length);
            writer.WriteString("sha256", hash);
            writer.WriteEndObject();
        });
        return Success(invocation, output);
    }

    private static async Task<ToolResult> AppendAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy,
        PinnedPath file,
        PinnedPath parent,
        CancellationToken ct)
    {
        var currentBytes = await ReadAllAsync(file.Handle, MaxFileSizeBytes, ct)
            .ConfigureAwait(false);
        ValidateExpectedHash(arguments, currentBytes);
        var encodingName = GetRequiredString(arguments, "encoding");
        var current = FileSystemTextEncoding.Decode(currentBytes, encodingName);
        var appended = FileSystemTextEncoding.NormalizeLineEndings(
            GetRequiredString(arguments, "content"),
            "preserve",
            current.LineEnding);
        var bytes = FileSystemTextEncoding.Encode(
            string.Concat(current.Content, appended),
            encodingName,
            current.HasBom);
        if (bytes.Length > MaxFileSizeBytes)
        {
            throw new InvalidDataException("The appended file exceeds the 1 MB limit.");
        }

        file.Dispose();
        await AtomicWriteAsync(parent, policy, bytes, overwrite: true, ct).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("appended", true);
            writer.WriteNumber("byteLength", bytes.Length);
            writer.WriteString("sha256", hash);
            writer.WriteEndObject();
        });
        return Success(invocation, output);
    }

    private static ToolResult CreateDirectory(
        ToolInvocation invocation,
        PinnedPath parent,
        BuiltinFsPathPolicy policy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(parent.Path))
        {
            throw new IOException("A file already exists at the requested directory path.");
        }

        var created = !Directory.Exists(parent.Path);
        if (created)
        {
            Directory.CreateDirectory(parent.Path);
        }

        _ = policy.ResolveWritePath(parent.Path);
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("created", created);
            writer.WriteEndObject();
        });
        return Success(invocation, output);
    }

    private static async Task<ToolResult> CopyAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy,
        PinnedPath source,
        PinnedPath destination,
        CancellationToken ct)
    {
        if (Directory.Exists(source.Path))
        {
            if (GetOptionalBoolean(arguments, "recursive") is not true)
            {
                throw new ArgumentException("Directory copy requires recursive to be true.");
            }

            if (File.Exists(destination.Path) || Directory.Exists(destination.Path))
            {
                throw new IOException("A directory copy cannot overwrite an existing destination.");
            }

            await CopyDirectoryAsync(source.Path, destination, policy, ct).ConfigureAwait(false);
            return Success(invocation, "{\"copied\":true}");
        }

        var length = RandomAccess.GetLength(source.Handle);
        if (length > MaxTransferFileSizeBytes)
        {
            throw new InvalidDataException("The file exceeds the 64 MB copy limit.");
        }

        var tempPath = CreateTempPath(destination);
        try
        {
            await using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                long offset = 0;
                while (offset < length)
                {
                    var count = (int)Math.Min(buffer.Length, length - offset);
                    var read = await RandomAccess.ReadAsync(
                        source.Handle,
                        buffer.AsMemory(0, count),
                        offset,
                        ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The source file changed during copy.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    offset += read;
                }

                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            _ = policy.ResolveWritePath(destination.Path);
            File.Move(tempPath, destination.Path, overwrite: permission.AllowOverwrite);
        }
        finally
        {
            DeleteTempFile(tempPath);
        }

        return Success(invocation, "{\"copied\":true}");
    }

    private static async Task CopyDirectoryAsync(
        string sourcePath,
        PinnedPath destination,
        BuiltinFsPathPolicy policy,
        CancellationToken ct)
    {
        var stagingPath = CreateTempPath(destination);
        try
        {
            Directory.CreateDirectory(stagingPath);
            var options = new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                MaxRecursionDepth = 32,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false
            };
            var entries = 0;
            long totalBytes = 0;
            foreach (var entry in new DirectoryInfo(sourcePath)
                         .EnumerateFileSystemInfos("*", options)
                         .OrderBy(static item => item.FullName, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > MaxListEntries)
                {
                    throw new InvalidDataException(
                        $"Directory copy exceeds the {MaxListEntries}-entry limit.");
                }

                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0
                    || entry.LinkTarget is not null)
                {
                    throw new UnauthorizedAccessException(
                        "Directory copy does not allow symbolic links or reparse points.");
                }

                _ = policy.ResolveReadPath(entry.FullName);
                var relative = Path.GetRelativePath(sourcePath, entry.FullName);
                var target = Path.Join(stagingPath, relative);
                if (entry is DirectoryInfo)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                var file = policy.PinReadFile(entry.FullName);
                using (file)
                {
                    var length = RandomAccess.GetLength(file.Handle);
                    totalBytes += length;
                    if (totalBytes > MaxTransferFileSizeBytes)
                    {
                        throw new InvalidDataException("Directory copy exceeds the 64 MB limit.");
                    }

                    await CopyHandleToFileAsync(file.Handle, target, length, ct)
                        .ConfigureAwait(false);
                }
            }

            ct.ThrowIfCancellationRequested();
            _ = policy.ResolveWritePath(destination.Path);
            Directory.Move(stagingPath, destination.Path);
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
    }

    private static async Task CopyHandleToFileAsync(
        Microsoft.Win32.SafeHandles.SafeFileHandle source,
        string destination,
        long length,
        CancellationToken ct)
    {
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            var read = await RandomAccess.ReadAsync(
                source,
                buffer.AsMemory(0, count),
                offset,
                ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The source file changed during copy.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            offset += read;
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static ToolResult Move(
        ToolInvocation invocation,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy,
        PinnedPath source,
        PinnedPath destination,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = policy.ResolveWritePath(source.Path);
        _ = policy.ResolveWritePath(destination.Path);
        if (Directory.Exists(source.Path))
        {
            if (File.Exists(destination.Path) || Directory.Exists(destination.Path))
            {
                throw new IOException("A directory move cannot overwrite an existing destination.");
            }

            Directory.Move(source.Path, destination.Path);
        }
        else if (File.Exists(source.Path))
        {
            if (Directory.Exists(destination.Path))
            {
                throw new IOException("The move destination is a directory.");
            }

            File.Move(source.Path, destination.Path, overwrite: permission.AllowOverwrite);
        }
        else
        {
            throw new FileNotFoundException("The move source was not found.", source.Path);
        }

        return Success(invocation, "{\"moved\":true}");
    }

    private static ToolResult Delete(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy,
        PinnedPath parent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = policy.ResolveWritePath(parent.Path);
        var deleted = false;
        if (File.Exists(parent.Path))
        {
            File.Delete(parent.Path);
            deleted = true;
        }
        else if (Directory.Exists(parent.Path))
        {
            var recursive = GetOptionalBoolean(arguments, "recursive") ?? false;
            if (recursive)
            {
                EnsureNoLinksInTree(parent.Path, policy, ct);
            }

            Directory.Delete(parent.Path, recursive);
            deleted = true;
        }

        return Success(invocation, $"{{\"deleted\":{deleted.ToString().ToLowerInvariant()}}}");
    }

    private static void EnsureNoLinksInTree(
        string path,
        BuiltinFsPathPolicy policy,
        CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            MaxRecursionDepth = 32,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false
        };
        var count = 0;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > MaxListEntries)
            {
                throw new InvalidDataException(
                    $"Recursive delete exceeds the {MaxListEntries}-entry limit.");
            }

            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0
                || entry.LinkTarget is not null)
            {
                throw new UnauthorizedAccessException(
                    "Recursive delete does not allow symbolic links or reparse points.");
            }

            _ = policy.ResolveWritePath(entry.FullName);
        }
    }

    private static async Task<ToolResult> SearchAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        PinnedPath directory,
        BuiltinFsPathPolicy policy,
        CancellationToken ct)
    {
        var query = GetRequiredString(arguments, "query");
        if (query.Length == 0)
        {
            throw new ArgumentException("Tool argument 'query' cannot be empty.");
        }

        var maxResults = GetOptionalInt32(arguments, "maxResults") ?? MaxSearchResults;
        if (maxResults is < 1 or > MaxSearchResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                $"Search results must be between 1 and {MaxSearchResults}.");
        }

        var encodingName = GetRequiredString(arguments, "encoding");
        var pattern = GetOptionalString(arguments, "pattern") ?? "*";
        if (pattern.Length is < 1 or > 256)
        {
            throw new ArgumentException("Tool argument 'pattern' must contain 1 to 256 characters.");
        }

        var comparison = GetOptionalBoolean(arguments, "caseSensitive") is true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var matches = new List<SearchMatch>();
        var scanned = 0;
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 32,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false
        };
        foreach (var candidate in Directory.EnumerateFiles(directory.Path, "*", enumerationOptions))
        {
            ct.ThrowIfCancellationRequested();
            var candidateRelative = Path.GetRelativePath(directory.Path, candidate)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!FileSystemName.MatchesSimpleExpression(
                    pattern,
                    candidateRelative,
                    ignoreCase: OperatingSystem.IsWindows()))
            {
                continue;
            }

            if (++scanned > MaxSearchFiles)
            {
                throw new InvalidDataException(
                    $"Search exceeds the {MaxSearchFiles}-file scan limit.");
            }

            PinnedPath file;
            try
            {
                file = policy.PinReadFile(candidate);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                or IOException
                or InvalidDataException)
            {
                continue;
            }

            using (file)
            {
                if (RandomAccess.GetLength(file.Handle) > MaxFileSizeBytes)
                {
                    continue;
                }

                string content;
                try
                {
                    var bytes = await ReadAllAsync(file.Handle, MaxFileSizeBytes, ct)
                        .ConfigureAwait(false);
                    content = FileSystemTextEncoding.Decode(bytes, encodingName).Content;
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                var lines = content.Split('\n');
                for (var index = 0; index < lines.Length; index++)
                {
                    var line = lines[index].TrimEnd('\r');
                    var column = line.IndexOf(query, comparison);
                    if (column < 0)
                    {
                        continue;
                    }

                    var relative = policy.GetTargetSummary(file.Path)["workspace:".Length..];
                    matches.Add(new SearchMatch(
                        relative,
                        index + 1,
                        column + 1,
                        line.Length <= 512 ? line : line[..512]));
                    if (matches.Count >= maxResults)
                    {
                        break;
                    }
                }
            }

            if (matches.Count >= maxResults)
            {
                break;
            }
        }

        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("matches");
            foreach (var match in matches)
            {
                writer.WriteStartObject();
                writer.WriteString("path", match.Path);
                writer.WriteNumber("line", match.Line);
                writer.WriteNumber("column", match.Column);
                writer.WriteString("preview", match.Preview);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        EnsureResultSize(output, "Search result");
        return Success(invocation, output);
    }

    private static async Task<ToolResult> EditAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy,
        PinnedPath file,
        PinnedPath parent,
        CancellationToken ct)
    {
        var bytes = await ReadAllAsync(file.Handle, MaxFileSizeBytes, ct).ConfigureAwait(false);
        ValidateExpectedHash(arguments, bytes);
        var encodingName = GetRequiredString(arguments, "encoding");
        var decoded = FileSystemTextEncoding.Decode(bytes, encodingName);
        var edits = ParseEdits(arguments, decoded);
        var content = new StringBuilder(decoded.Content);
        foreach (var edit in edits.OrderByDescending(static item => item.Offset))
        {
            content.Remove(edit.Offset, edit.Length);
            content.Insert(edit.Offset, edit.Content);
        }

        var editedBytes = FileSystemTextEncoding.Encode(
            content.ToString(),
            encodingName,
            decoded.HasBom);
        if (editedBytes.Length > MaxFileSizeBytes)
        {
            throw new InvalidDataException("The edited file exceeds the 1 MB limit.");
        }

        file.Dispose();
        await AtomicWriteAsync(parent, policy, editedBytes, overwrite: true, ct)
            .ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(editedBytes));
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("edited", true);
            writer.WriteNumber("byteLength", editedBytes.Length);
            writer.WriteString("sha256", hash);
            writer.WriteEndObject();
        });
        return Success(invocation, output);
    }

    private static async Task<byte[]> ReadAllAsync(
        SafeFileHandle handle,
        int maxBytes,
        CancellationToken ct)
    {
        var length = RandomAccess.GetLength(handle);
        if (length > maxBytes)
        {
            throw new InvalidDataException(
                $"The file exceeds the {maxBytes / (1024 * 1024)} MB read limit.");
        }

        var bytes = new byte[checked((int)length)];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = await RandomAccess.ReadAsync(
                handle,
                bytes.AsMemory(total),
                total,
                ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The file changed while it was being read.");
            }

            total += read;
        }

        return bytes;
    }

    private static async Task AtomicWriteAsync(
        PinnedPath parent,
        BuiltinFsPathPolicy policy,
        ReadOnlyMemory<byte> content,
        bool overwrite,
        CancellationToken ct)
    {
        var tempPath = CreateTempPath(parent);
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            _ = policy.ResolveWritePath(parent.Path);
            File.Move(tempPath, parent.Path, overwrite);
        }
        finally
        {
            DeleteTempFile(tempPath);
        }
    }

    private static string CreateTempPath(PinnedPath parent) => Path.Join(
        parent.PinnedTargetPath,
        $".{Path.GetFileName(parent.Path)}.{Guid.NewGuid():N}.tmp");

    private static void DeleteTempFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static TextEdit[] ParseEdits(
        JsonElement arguments,
        DecodedText decoded)
    {
        if (!arguments.TryGetProperty("operations", out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Tool argument 'operations' must be an array.");
        }

        var operations = property.EnumerateArray().ToArray();
        if (operations.Length is < 1 or > 100)
        {
            throw new ArgumentException("Edit operations must contain between 1 and 100 items.");
        }

        var lines = GetLineSpans(decoded.Content);
        if (lines.Count == 0)
        {
            throw new InvalidDataException("The file is empty. Use mcli_fs_write instead.");
        }

        var newline = decoded.LineEnding switch
        {
            "crlf" => "\r\n",
            "cr" => "\r",
            _ => "\n"
        };
        var edits = new List<TextEdit>(operations.Length);
        foreach (var operation in operations)
        {
            if (operation.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Every edit operation must be an object.");
            }

            var type = GetRequiredString(operation, "type");
            switch (type)
            {
                case "replace":
                {
                    var (start, end) = GetRequiredLineRange(operation, lines);
                    var content = FileSystemTextEncoding.NormalizeLineEndings(
                        GetRequiredString(operation, "content"),
                        "preserve",
                        decoded.LineEnding);
                    edits.Add(new TextEdit(
                        lines[start - 1].Start,
                        lines[end - 1].ContentEnd - lines[start - 1].Start,
                        content));
                    break;
                }
                case "delete":
                {
                    var (start, end) = GetRequiredLineRange(operation, lines);
                    edits.Add(new TextEdit(
                        lines[start - 1].Start,
                        lines[end - 1].FullEnd - lines[start - 1].Start,
                        string.Empty));
                    break;
                }
                case "insertBefore":
                case "insertAfter":
                {
                    var line = GetRequiredInt32(operation, "line");
                    ValidateLine(line, lines.Count);
                    var content = FileSystemTextEncoding.NormalizeLineEndings(
                        GetRequiredString(operation, "content"),
                        "preserve",
                        decoded.LineEnding);
                    var span = lines[line - 1];
                    if (string.Equals(type, "insertBefore", StringComparison.Ordinal))
                    {
                        edits.Add(new TextEdit(
                            span.Start,
                            0,
                            string.Concat(content, newline)));
                    }
                    else if (span.FullEnd > span.ContentEnd)
                    {
                        edits.Add(new TextEdit(
                            span.FullEnd,
                            0,
                            string.Concat(content, newline)));
                    }
                    else
                    {
                        edits.Add(new TextEdit(
                            span.ContentEnd,
                            0,
                            string.Concat(newline, content)));
                    }

                    break;
                }
                default:
                    throw new ArgumentException(
                        $"Edit operation '{type}' is not supported.");
            }
        }

        var ordered = edits.OrderBy(static item => item.Offset).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            if (current.Offset < previous.Offset + previous.Length
                || current.Offset == previous.Offset)
            {
                throw new ArgumentException("Edit operations overlap.");
            }
        }

        return ordered;
    }

    private static List<LineSpan> GetLineSpans(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var lines = new List<LineSpan>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r')
            {
                var contentEnd = index;
                var fullEnd = index + 1;
                if (fullEnd < content.Length && content[fullEnd] == '\n')
                {
                    fullEnd++;
                    index++;
                }

                lines.Add(new LineSpan(start, contentEnd, fullEnd));
                start = fullEnd;
            }
            else if (content[index] == '\n')
            {
                lines.Add(new LineSpan(start, index, index + 1));
                start = index + 1;
            }
        }

        if (start < content.Length)
        {
            lines.Add(new LineSpan(start, content.Length, content.Length));
        }
        else
        {
            lines.Add(new LineSpan(start, start, start));
        }

        return lines;
    }

    private static (int Start, int End) GetRequiredLineRange(
        JsonElement operation,
        IReadOnlyList<LineSpan> lines)
    {
        var start = GetRequiredInt32(operation, "startLine");
        var end = GetRequiredInt32(operation, "endLine");
        ValidateLine(start, lines.Count);
        ValidateLine(end, lines.Count);
        if (end < start)
        {
            throw new ArgumentException("Edit endLine must be greater than or equal to startLine.");
        }

        return (start, end);
    }

    private static void ValidateLine(int line, int lineCount)
    {
        if (line < 1 || line > lineCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(line),
                $"Line must be between 1 and {lineCount}.");
        }
    }

    private static void ValidateExpectedHash(JsonElement arguments, ReadOnlySpan<byte> bytes)
    {
        var expectedHash = GetRequiredString(arguments, "expectedHash");
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Tool argument 'expectedHash' must be a SHA-256 hex value.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The expected SHA-256 does not match the current file ({actualHash}).");
        }
    }

    private static string DetectContentKind(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0))
        {
            return "binary";
        }

        try
        {
            _ = FileSystemTextEncoding.Decode(bytes, "auto");
            return "text";
        }
        catch (InvalidDataException)
        {
            return "unknown";
        }
    }

    private static bool ResolveBom(
        JsonElement arguments,
        bool existingHasBom,
        bool isExisting)
    {
        var mode = GetOptionalString(arguments, "bom") ?? "preserve";
        return mode switch
        {
            "preserve" => isExisting && existingHasBom,
            "add" => true,
            "remove" => false,
            _ => throw new ArgumentException("Tool argument 'bom' must be preserve, add, or remove.")
        };
    }

    private static int GetRequiredInt32(JsonElement arguments, string propertyName) =>
        GetOptionalInt32(arguments, propertyName)
        ?? throw new ArgumentException($"Tool argument '{propertyName}' must be an integer.");

    private static int? GetOptionalInt32(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be an integer.");
        }

        return value;
    }

    private static bool? GetOptionalBoolean(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be a boolean.");
        }

        return property.GetBoolean();
    }

    private static void EnsureResultSize(string output, string description)
    {
        if (Utf8.GetByteCount(output) > MaxFileSizeBytes)
        {
            throw new InvalidDataException($"{description} exceeds the 1 MB result limit.");
        }
    }

    private static ToolResult Success(ToolInvocation invocation, string output) =>
        new(invocation.CallId, invocation.ToolId, true, output);

    private sealed class FsPreparedExecution(
        string targetSummary,
        Func<CancellationToken, Task<ToolResult>> execute,
        IDisposable[] resources) : IPreparedToolExecution
    {
        private readonly Func<CancellationToken, Task<ToolResult>> _execute = execute;
        private readonly IDisposable[] _resources = resources;
        private int _disposed;

        public string TargetSummary { get; } = targetSummary;

        public Task<ToolResult> ExecuteAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _execute(ct);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                for (var index = _resources.Length - 1; index >= 0; index--)
                {
                    _resources[index].Dispose();
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record SearchMatch(string Path, int Line, int Column, string Preview);

    private sealed record TextEdit(int Offset, int Length, string Content);

    private sealed record LineSpan(int Start, int ContentEnd, int FullEnd);
}
