using System.Diagnostics;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class SkillScriptRunnerTests
{
    private static readonly string[] ScriptExtensions = [".ps1", ".sh", ".py", ".js", ".cs", ".csx"];
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "madorin-script-tests", Guid.NewGuid().ToString("N"));
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
    [DataRow(".ps1")]
    [DataRow(".sh")]
    [DataRow(".py")]
    [DataRow(".js")]
    [DataRow(".cs")]
    [DataRow(".csx")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SupportedInterpreterExecutesUnicodePathAndArgumentArray(string extension)
    {
        await using var catalog = await CreateCatalogAsync("arguments");
        try
        {
            _ = await AgentFileSkillScriptRunner.ResolveInterpreterNameAsync(extension);
        }
        catch (SkillScriptException ex) when (ex.Code == RuntimeErrorCodes.SkillScriptInterpreterUnavailable)
        {
            Assert.Inconclusive($"No interpreter is installed for '{extension}'.");
            return;
        }

        var runner = new AgentFileSkillScriptRunner(catalog.Current, WorkspaceRoot, "run-1");
        var result = await runner.RunAsync(new SkillScriptRequest(
            "script-test",
            $"scripts/run{extension}",
            ["参数 一", "quoted\"value"],
            catalog.Current.Version,
            "run-1",
            AllowedEnvironmentVariables: SkillScriptEnvironmentPolicy.InheritedVariableNames));

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        StringAssert.Contains(result.StandardOutput, "参数 一|quoted\"value");
    }

    [TestMethod]
    public async Task InterpreterProbePropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => AgentFileSkillScriptRunner.ResolveInterpreterNameAsync(".cs", cts.Token));
    }

    [TestMethod]
    public async Task MissingInterpreterReturnsStructuredError()
    {
        foreach (var extension in ScriptExtensions)
        {
            try
            {
                _ = await AgentFileSkillScriptRunner.ResolveInterpreterNameAsync(extension);
            }
            catch (SkillScriptException ex)
                when (ex.Code == RuntimeErrorCodes.SkillScriptInterpreterUnavailable)
            {
                Assert.AreEqual(RuntimeErrorCodes.SkillScriptInterpreterUnavailable, ex.Code);
                return;
            }
        }

        Assert.Inconclusive("All six optional interpreters are installed on this machine.");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task NonZeroExitCodePreservesStandardError()
    {
        await using var catalog = await CreateCatalogAsync("nonzero");
        var script = await SelectAvailableScriptAsync();
        var runner = new AgentFileSkillScriptRunner(catalog.Current, WorkspaceRoot, "run-1");

        var result = await runner.RunAsync(new SkillScriptRequest(
            "script-test",
            script,
            CatalogVersion: catalog.Current.Version,
            RunId: "run-1",
            AllowedEnvironmentVariables: SkillScriptEnvironmentPolicy.InheritedVariableNames));

        Assert.AreEqual(7, result.ExitCode);
        StringAssert.Contains(result.StandardError, "expected-error");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TimeoutKillsTheProcessTree()
    {
        await using var catalog = await CreateCatalogAsync("sleep");
        var script = await SelectAvailableScriptAsync();
        var runner = new AgentFileSkillScriptRunner(catalog.Current, WorkspaceRoot, "run-1");
        var started = Stopwatch.GetTimestamp();

        var exception = await Assert.ThrowsExactlyAsync<SkillScriptException>(() => runner.RunAsync(
            new SkillScriptRequest(
                "script-test",
                script,
                CatalogVersion: catalog.Current.Version,
                RunId: "run-1",
                Timeout: TimeSpan.FromMilliseconds(250),
                AllowedEnvironmentVariables: SkillScriptEnvironmentPolicy.InheritedVariableNames)));

        Assert.AreEqual(RuntimeErrorCodes.ToolExecutionTimedOut, exception.Code);
        Assert.IsLessThan(TimeSpan.FromSeconds(10), Stopwatch.GetElapsedTime(started));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CallerCancellationPropagatesAfterKillingTheProcessTree()
    {
        await using var catalog = await CreateCatalogAsync("sleep");
        var script = await SelectAvailableScriptAsync();
        var runner = new AgentFileSkillScriptRunner(catalog.Current, WorkspaceRoot, "run-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
            new SkillScriptRequest(
                "script-test",
                script,
                CatalogVersion: catalog.Current.Version,
                RunId: "run-1",
                Timeout: TimeSpan.FromSeconds(20),
                AllowedEnvironmentVariables: SkillScriptEnvironmentPolicy.InheritedVariableNames),
            cts.Token));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task OutputLimitKillsTheProcessWithoutWaitingForTimeout()
    {
        await using var catalog = await CreateCatalogAsync("large-output");
        var script = await SelectAvailableScriptAsync();
        var runner = new AgentFileSkillScriptRunner(catalog.Current, WorkspaceRoot, "run-1")
        {
            MaxOutputBytes = 1024
        };
        var started = Stopwatch.GetTimestamp();

        var exception = await Assert.ThrowsExactlyAsync<SkillScriptException>(() => runner.RunAsync(
            new SkillScriptRequest(
                "script-test",
                script,
                CatalogVersion: catalog.Current.Version,
                RunId: "run-1",
                Timeout: TimeSpan.FromSeconds(20),
                AllowedEnvironmentVariables: SkillScriptEnvironmentPolicy.InheritedVariableNames)));

        Assert.AreEqual(RuntimeErrorCodes.ToolExecutionFailed, exception.Code);
        Assert.IsLessThan(TimeSpan.FromSeconds(10), Stopwatch.GetElapsedTime(started));
    }

    [TestMethod]
    public async Task RunnerRejectsInvocationFromAnotherRunBeforeStartingInterpreter()
    {
        await using var catalog = await CreateCatalogAsync("arguments");
        var runner = new AgentFileSkillScriptRunner(catalog.Current, WorkspaceRoot, "run-owner");

        var exception = await Assert.ThrowsExactlyAsync<SkillScriptException>(() => runner.RunAsync(
            new SkillScriptRequest(
                "script-test",
                "scripts/run.cs",
                CatalogVersion: catalog.Current.Version,
                RunId: "run-other")));

        Assert.AreEqual(RuntimeErrorCodes.ToolIntentInvalid, exception.Code);
    }

    [TestMethod]
    public async Task DynamicToolExecutor_UsesTheInvocationCatalogAfterRefresh()
    {
        await using var catalog = await CreateCatalogAsync("arguments");
        var captured = catalog.Current;
        var sourceSkillFile = Path.Combine(
            WorkspaceRoot,
            ".madorin",
            "skills",
            "script-test",
            "SKILL.md");
        await File.AppendAllTextAsync(sourceSkillFile, "Catalog version two.\n");
        var refreshed = await catalog.RefreshAsync();
        Assert.AreNotEqual(captured.Version, refreshed.Version);

        var snapshots = new Dictionary<string, SkillCatalogSnapshot>(StringComparer.Ordinal)
        {
            [captured.Version] = captured,
            [refreshed.Version] = refreshed
        };
        var executor = new AgentFileSkillScriptToolExecutor(
            WorkspaceRoot,
            "run-1",
            version => snapshots.GetValueOrDefault(version));
        var skill = Assert.ContainsSingle(captured.Skills);
        var script = "scripts/run.cs";
        var scriptPath = Path.Combine(
            skill.MaterializedPath,
            script.Replace('/', Path.DirectorySeparatorChar));
        var interpreter = await AgentFileSkillScriptRunner.ResolveInterpreterNameAsync(".cs");
        var scriptHash = SkillScriptAuthorization.ComputeScriptHash(scriptPath);
        var invocation = new ToolInvocation(
            "call-1",
            SkillScriptAuthorization.ToolId,
            "agent-1",
            ParentAgentId: null,
            $$"""
            {"skillName":"{{skill.Name}}","script":"{{script}}","scriptHash":"{{scriptHash}}","interpreter":"{{interpreter}}","catalogVersion":"{{captured.Version}}","arguments":[]}
            """,
            "run-1",
            "session-1",
            "invocation-1");

        await using var prepared = await executor.PrepareAsync(invocation);

        Assert.AreEqual("skill:script-test/scripts/run.cs", prepared.TargetSummary);
    }

    private string WorkspaceRoot => Path.Combine(_root, "中文 工作区");

    private async Task<SkillCatalogService> CreateCatalogAsync(string scenario)
    {
        var source = Path.Combine(WorkspaceRoot, ".madorin", "skills");
        var skillDirectory = Path.Combine(source, "script-test");
        var scriptsDirectory = Path.Combine(skillDirectory, "scripts");
        Directory.CreateDirectory(scriptsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(skillDirectory, "SKILL.md"),
            "---\nname: script-test\ndescription: Script runner tests\n---\nRun the requested script.\n");
        foreach (var extension in ScriptExtensions)
        {
            await File.WriteAllTextAsync(
                Path.Combine(scriptsDirectory, $"run{extension}"),
                GetScript(scenario, extension));
        }

        var catalog = new SkillCatalogService(
            WorkspaceRoot,
            Path.Combine(_root, "data"),
            $"runtime-{scenario}",
            sourceScope: SkillCatalogSourceScope.Workspace);
        try
        {
            await catalog.InitializeAsync();
            return catalog;
        }
        catch
        {
            await catalog.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> SelectAvailableScriptAsync()
    {
        foreach (var extension in ScriptExtensions)
        {
            try
            {
                _ = await AgentFileSkillScriptRunner.ResolveInterpreterNameAsync(extension);
                return $"scripts/run{extension}";
            }
            catch (SkillScriptException ex)
                when (ex.Code == RuntimeErrorCodes.SkillScriptInterpreterUnavailable)
            {
            }
        }

        throw new AssertInconclusiveException("No supported Skill script interpreter is installed.");
    }

    private static string GetScript(string scenario, string extension) =>
        (scenario, extension) switch
        {
            ("arguments", ".ps1") => "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\nWrite-Output ($args -join '|')\n",
            ("arguments", ".sh") => "printf '%s|%s\\n' \"$1\" \"$2\"\n",
            ("arguments", ".py") => "import sys\nprint('|'.join(sys.argv[1:]))\n",
            ("arguments", ".js") => "console.log(process.argv.slice(2).join('|'));\n",
            ("arguments", ".cs") => "System.Console.WriteLine(string.Join(\"|\", args));\n",
            ("arguments", ".csx") => "System.Console.WriteLine(string.Join(\"|\", Args));\n",
            ("nonzero", ".ps1") => "[Console]::Error.WriteLine('expected-error')\nexit 7\n",
            ("nonzero", ".sh") => "printf 'expected-error\\n' >&2\nexit 7\n",
            ("nonzero", ".py") => "import sys\nsys.stderr.write('expected-error\\n')\nsys.exit(7)\n",
            ("nonzero", ".js") => "console.error('expected-error');\nprocess.exit(7);\n",
            ("nonzero", ".cs") => "System.Console.Error.WriteLine(\"expected-error\");\nSystem.Environment.Exit(7);\n",
            ("nonzero", ".csx") => "System.Console.Error.WriteLine(\"expected-error\");\nSystem.Environment.Exit(7);\n",
            ("sleep", ".ps1") => "Start-Sleep -Seconds 30\n",
            ("sleep", ".sh") => "sleep 30\n",
            ("sleep", ".py") => "import time\ntime.sleep(30)\n",
            ("sleep", ".js") => "setTimeout(() => {}, 30000);\n",
            ("sleep", ".cs") => "await System.Threading.Tasks.Task.Delay(30000);\n",
            ("sleep", ".csx") => "System.Threading.Thread.Sleep(30000);\n",
            ("large-output", ".ps1") => "[Console]::Out.Write(('x' * 100000))\n",
            ("large-output", ".sh") => "i=0; while [ $i -lt 100000 ]; do printf x; i=$((i+1)); done\n",
            ("large-output", ".py") => "print('x' * 100000)\n",
            ("large-output", ".js") => "process.stdout.write('x'.repeat(100000));\n",
            ("large-output", ".cs") => "System.Console.Write(new string('x', 100000));\n",
            ("large-output", ".csx") => "System.Console.Write(new string('x', 100000));\n",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
}
