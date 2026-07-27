using System.Security.Cryptography;
using System.Text;

using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Testing;

public sealed class FixtureLibrary(
    string fixtureRoot,
    string executablePath,
    ProcessRunner processRunner)
{
    public const string OwnershipMarker = ".mconsole-owned";

    private readonly string _fixtureRoot = Path.GetFullPath(fixtureRoot);
    private readonly string _executablePath = Path.GetFullPath(executablePath);
    private readonly ProcessRunner _processRunner = processRunner;

    public async Task ExecuteAsync(
        string action,
        CaseExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(context);
        switch (action)
        {
            case "fresh-workspace":
                await CreateFreshWorkspaceAsync(context, cancellationToken);
                break;
            case "seed-sessions":
                await SeedSessionAsync(context, cancellationToken);
                break;
            case "seed-orphan-blob":
                await SeedOrphanBlobAsync(context, cancellationToken);
                break;
            case "db-backup":
                await BackupAsync(context, cancellationToken);
                break;
            case "restore-backup":
                RestoreBackup(context);
                break;
            case "clear-memory":
                await ClearMemoryAsync(context, cancellationToken);
                break;
            case "cleanup-owned-workspace":
                CleanupOwnedWorkspace(context);
                break;
            default:
                throw new InvalidDataException($"Unknown fixture action '{action}'.");
        }
    }

    public async Task CreateFreshWorkspaceAsync(
        CaseExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_fixtureRoot);
        var workspace = Path.Combine(_fixtureRoot, "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var markerValue = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(
            Path.Combine(workspace, OwnershipMarker),
            markerValue,
            Encoding.UTF8,
            cancellationToken);
        context.Set("workspace", workspace);
        context.Set("dataDir", Path.Combine(workspace, ".madorin"));
        context.Set("fixtureOwner", markerValue);
    }

    public async Task SeedSessionAsync(
        CaseExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var workspace = RequireOwnedWorkspace(context);
        var result = await _processRunner.RunAsync(
            _executablePath,
            [
                "run", "--workspace", workspace,
                "--input", "MConsole fixture seed",
                "--no-stream", "--json", "--no-color",
            ],
            workspace,
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken);
        if (!result.IsSuccess || result.Envelope?.Data is not { } data)
        {
            throw new InvalidOperationException(
                $"Fixture session seed failed with exit code {result.ExitCode}.");
        }

        context.CaptureStepData("fixture.seed-sessions", data);
    }

    public async Task SeedOrphanBlobAsync(
        CaseExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var workspace = RequireOwnedWorkspace(context);
        var content = Encoding.UTF8.GetBytes("mconsole-orphan-blob-" + Guid.NewGuid().ToString("N"));
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var blobDirectory = Path.Combine(workspace, ".madorin", "blobs");
        Directory.CreateDirectory(blobDirectory);
        var path = Path.Combine(blobDirectory, hash);
        await File.WriteAllBytesAsync(path, content, cancellationToken);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        context.Set("orphanBlobHash", hash);
        context.Set("orphanBlobPath", path);
    }

    public async Task BackupAsync(
        CaseExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var workspace = RequireOwnedWorkspace(context);
        var backup = Path.Combine(workspace, "mconsole-state-backup.db");
        var result = await _processRunner.RunAsync(
            _executablePath,
            ["db", "backup", "--workspace", workspace, "--output", backup, "--json", "--no-color"],
            workspace,
            timeout: TimeSpan.FromMinutes(1),
            cancellationToken: cancellationToken);
        if (!result.IsSuccess || !File.Exists(backup))
        {
            throw new InvalidOperationException(
                $"Fixture database backup failed with exit code {result.ExitCode}.");
        }

        context.Set("backupPath", backup);
    }

    public void RestoreBackup(CaseExecutionContext context)
    {
        var workspace = RequireOwnedWorkspace(context);
        var backup = RequireValue(context, "backupPath");
        var target = Path.Combine(workspace, ".madorin", "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(backup, target, overwrite: true);
    }

    public async Task ClearMemoryAsync(
        CaseExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var workspace = RequireOwnedWorkspace(context);
        var result = await _processRunner.RunAsync(
            _executablePath,
            ["memory", "clear", "project", "--workspace", workspace, "--yes", "--json", "--no-color"],
            workspace,
            timeout: TimeSpan.FromMinutes(1),
            cancellationToken: cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Fixture memory clear failed with exit code {result.ExitCode}.");
        }
    }

    public void CleanupOwnedWorkspace(CaseExecutionContext context)
    {
        var workspace = RequireOwnedWorkspace(context);
        Directory.Delete(workspace, recursive: true);
    }

    private string RequireOwnedWorkspace(CaseExecutionContext context)
    {
        var workspace = Path.GetFullPath(RequireValue(context, "workspace"));
        var root = _fixtureRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!workspace.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Fixture workspace is outside the owned fixture root.");
        }

        var markerPath = Path.Combine(workspace, OwnershipMarker);
        if (!File.Exists(markerPath)
            || !string.Equals(
                File.ReadAllText(markerPath).Trim(),
                RequireValue(context, "fixtureOwner"),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fixture workspace ownership marker is missing or invalid.");
        }

        return workspace;
    }

    private static string RequireValue(CaseExecutionContext context, string key) =>
        context.Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Fixture context value '{key}' is missing.");
}
