using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Tools;

namespace Madorin.AI.Runtime.Services.Skills;

public sealed record SkillScriptRequest(
    string SkillName,
    string Script,
    IReadOnlyList<string>? Arguments = null,
    string? CatalogVersion = null,
    string? RunId = null,
    TimeSpan? Timeout = null,
    IReadOnlySet<string>? AllowedEnvironmentVariables = null,
    string? ScriptHash = null,
    string? Interpreter = null);

public sealed record SkillScriptExecutionResult(
    string SkillName,
    string Script,
    string Interpreter,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    string CatalogVersion);

public sealed class SkillScriptException(string code, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Executes scripts from the immutable materialized Skill snapshot using an
/// argument array. Authorization is intentionally left to ToolGateway callers;
/// this class never accepts a raw shell command line.
/// </summary>
public sealed class AgentFileSkillScriptRunner
{
    private const int DefaultOutputLimit = 4 * 1024 * 1024;
    private readonly SkillCatalogSnapshot _snapshot;
    private readonly string _workspaceRoot;
    private readonly string? _runId;

    public AgentFileSkillScriptRunner(
        SkillCatalogSnapshot snapshot,
        string workspaceRoot,
        string? runId = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _runId = runId;
    }

    public AgentFileSkillScriptRunner(
        SkillCatalogService catalog,
        string workspaceRoot,
        string? runId = null)
        : this((catalog ?? throw new ArgumentNullException(nameof(catalog))).Current, workspaceRoot, runId)
    {
    }

    public int MaxOutputBytes { get; init; } = DefaultOutputLimit;

    public async Task<SkillScriptExecutionResult> RunAsync(
        SkillScriptRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_snapshot.TryGetSkill(request.SkillName, out var skill) || skill is null)
        {
            throw new SkillScriptException(RuntimeErrorCodes.SkillScriptUnsupported, $"Skill '{request.SkillName}' was not found.");
        }

        if (_runId is not null
            && !string.Equals(request.RunId, _runId, StringComparison.Ordinal))
        {
            throw new SkillScriptException(
                RuntimeErrorCodes.ToolIntentInvalid,
                "The Skill script invocation does not belong to this Run.");
        }

        if (request.CatalogVersion is not null
            && !string.Equals(request.CatalogVersion, _snapshot.Version, StringComparison.Ordinal))
        {
            throw new SkillScriptException(
                RuntimeErrorCodes.SkillRefreshFailed,
                "The Skill catalog changed before script execution.");
        }

        var relativeScript = NormalizeRelative(request.Script);
        if (!skill.Scripts.Contains(relativeScript, StringComparer.OrdinalIgnoreCase))
        {
            throw new SkillScriptException(RuntimeErrorCodes.SkillScriptUnsupported, $"Script '{request.Script}' is not declared by Skill '{request.SkillName}'.");
        }

        var scriptPath = EnsureWithin(skill.MaterializedPath, relativeScript);
        var mapping = await ResolveInterpreterAsync(Path.GetExtension(scriptPath), ct).ConfigureAwait(false);
        if (request.Interpreter is not null
            && !string.Equals(request.Interpreter, mapping.FileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new SkillScriptException(
                RuntimeErrorCodes.SkillScriptInterpreterUnavailable,
                $"Requested interpreter '{request.Interpreter}' does not match '{mapping.FileName}'.");
        }

        if (request.ScriptHash is not null
            && !string.Equals(
                request.ScriptHash,
                SkillScriptAuthorization.ComputeScriptHash(scriptPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SkillScriptException(
                RuntimeErrorCodes.SkillRefreshFailed,
                "The materialized script content hash does not match the authorized hash.");
        }
        var timeout = request.Timeout ?? TimeSpan.FromMinutes(5);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (MaxOutputBytes <= 0)
        {
            throw new InvalidOperationException("The Skill script output limit must be positive.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = mapping.FileName,
            WorkingDirectory = _workspaceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var argument in mapping.PrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(scriptPath);
        if (mapping.AppendArgumentSeparator)
        {
            startInfo.ArgumentList.Add("--");
        }

        foreach (var argument in request.Arguments ?? [])
        {
            startInfo.ArgumentList.Add(argument ?? string.Empty);
        }

        RestrictEnvironment(startInfo, request.AllowedEnvironmentVariables);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new SkillScriptException(RuntimeErrorCodes.SkillScriptInterpreterUnavailable, $"Interpreter '{mapping.FileName}' could not be started.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new SkillScriptException(RuntimeErrorCodes.SkillScriptInterpreterUnavailable, $"Interpreter '{mapping.FileName}' is unavailable.", ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var outputBudget = new OutputBudget(MaxOutputBytes);
            var outputTask = ReadLimitedAsync(process.StandardOutput, process, outputBudget, timeoutCts.Token);
            var errorTask = ReadLimitedAsync(process.StandardError, process, outputBudget, timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new SkillScriptExecutionResult(
                request.SkillName,
                relativeScript,
                mapping.FileName,
                process.ExitCode,
                output,
                error,
                Stopwatch.GetElapsedTime(started),
                request.CatalogVersion ?? _snapshot.Version);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKill(process);
            if (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
            }

            throw new SkillScriptException(RuntimeErrorCodes.ToolExecutionTimedOut, $"Skill script exceeded the {timeout.TotalSeconds:0.#}-second timeout.");
        }
        catch (SkillScriptException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
            }
        }
    }

    public static async Task<string> ResolveInterpreterNameAsync(string extension, CancellationToken ct = default) =>
        (await ResolveInterpreterAsync(extension, ct).ConfigureAwait(false)).FileName;

    private static async Task<InterpreterMapping> ResolveInterpreterAsync(string extension, CancellationToken ct)
    {
        string[] candidates = extension.ToLowerInvariant() switch
        {
            ".ps1" => OperatingSystem.IsWindows() ? new[] { "pwsh", "powershell.exe" } : new[] { "pwsh" },
            ".sh" => new[] { "bash", "sh" },
            ".py" => new[] { "python3", "python" },
            ".js" => new[] { "node" },
            ".cs" => new[] { "dotnet" },
            ".csx" => new[] { "dotnet-script" },
            _ => Array.Empty<string>()
        };
        if (candidates.Length == 0)
        {
            throw new SkillScriptException(RuntimeErrorCodes.SkillScriptUnsupported, $"Script extension '{extension}' is not supported.");
        }

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await CanStartAsync(candidate, ct).ConfigureAwait(false))
            {
                var prefix = extension.ToLowerInvariant() switch
                {
                    ".ps1" => new[]
                    {
                        "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"
                    },
                    ".cs" => new[] { "run", "--file" },
                    _ => Array.Empty<string>()
                };
                return new InterpreterMapping(
                    candidate,
                    prefix,
                    extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".csx", StringComparison.OrdinalIgnoreCase));
            }
        }

        throw new SkillScriptException(RuntimeErrorCodes.SkillScriptInterpreterUnavailable, $"No interpreter is available for '{extension}'.");
    }

    private static async Task<bool> CanStartAsync(string executable, CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        try
        {
            if (!process.Start())
            {
                return false;
            }

            await process.WaitForExitAsync(probeCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return false;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static void RestrictEnvironment(ProcessStartInfo startInfo, IReadOnlySet<string>? allowed)
    {
        var values = startInfo.Environment.ToDictionary(static x => x.Key, static x => x.Value, StringComparer.OrdinalIgnoreCase);
        startInfo.Environment.Clear();
        foreach (var name in allowed ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
            if (SkillScriptEnvironmentPolicy.FixedVariableValues.TryGetValue(name, out var fixedValue))
            {
                startInfo.Environment[name] = fixedValue;
            }
            else if (values.TryGetValue(name, out var value) && value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        if (allowed?.Contains("HOME") == true
            && !startInfo.Environment.ContainsKey("HOME")
            && startInfo.Environment.TryGetValue("USERPROFILE", out var userProfile))
        {
            startInfo.Environment["HOME"] = userProfile;
        }

        if (allowed?.Contains("DOTNET_CLI_HOME") == true
            && !startInfo.Environment.ContainsKey("DOTNET_CLI_HOME")
            && startInfo.Environment.TryGetValue("HOME", out var dotnetHome))
        {
            startInfo.Environment["DOTNET_CLI_HOME"] = dotnetHome;
        }
    }

    private static async Task<string> ReadLimitedAsync(
        StreamReader reader,
        Process process,
        OutputBudget budget,
        CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new char[16 * 1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0)
            {
                return builder.ToString();
            }

            if (!budget.TryConsume(Encoding.UTF8.GetByteCount(buffer, 0, count)))
            {
                TryKill(process);
                throw new SkillScriptException(RuntimeErrorCodes.ToolExecutionFailed, "Skill script output exceeded the configured limit.");
            }

            builder.Append(buffer, 0, count);
        }
    }

    private static string NormalizeRelative(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(static x => x is "." or ".."))
        {
            throw new SkillScriptException(RuntimeErrorCodes.SkillResourceBlocked, "Script path traversal is not allowed.");
        }

        return normalized;
    }

    private static string EnsureWithin(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new SkillScriptException(RuntimeErrorCodes.SkillResourceBlocked, "Script path escapes the materialized Skill directory.");
        }

        return path;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    private sealed class OutputBudget(int maxBytes)
    {
        private int _consumedBytes;

        public bool TryConsume(int bytes) =>
            Interlocked.Add(ref _consumedBytes, bytes) <= maxBytes;
    }

    private sealed record InterpreterMapping(
        string FileName,
        IReadOnlyList<string> PrefixArguments,
        bool AppendArgumentSeparator);
}
