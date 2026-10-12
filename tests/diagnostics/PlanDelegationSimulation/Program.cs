using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length != 2 || args[0] != "--scenario" || args[1] is not ("files" or "parallel" or "policy"))
{
    Console.Error.WriteLine("Usage: --scenario files|parallel|policy");
    return 2;
}
var scenario = args[1];
var repo = new DirectoryInfo(AppContext.BaseDirectory);
while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Madorin.AI.Runtime.slnx")))
{
    repo = repo.Parent;
}
var repoRoot = repo?.FullName ?? throw new DirectoryNotFoundException("Repository not found.");
var cli = Path.Combine(repoRoot, "src/Madorin.AI.Runtime.Cli/bin/Debug/net10.0/madorin-cli.exe");
var output = Path.Combine(repoRoot, ".artifacts", $"expert-{scenario}-{DateTime.Now:yyyyMMdd-HHmmss}");
var workspace = Path.Combine(output, "workspace");
var home = Path.Combine(output, "home");
var configDirectory = Path.Combine(home, ".madorin");
var configCopy = Path.Combine(configDirectory, "config.json");
Directory.CreateDirectory(workspace);
Directory.CreateDirectory(Path.Combine(configDirectory, "agents"));
Console.WriteLine($"EVIDENCE_DIRECTORY|{output}");
var exit = -1;
try
{
    var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".madorin/config.json");
    var config = JsonNode.Parse(await File.ReadAllTextAsync(source))!.AsObject();
    config["defaultAgent"] = "planner";
    config["agents"] = new JsonArray();
    await File.WriteAllTextAsync(configCopy, config.ToJsonString());
    foreach (var id in new[] { "planner", "existing-reader" })
    {
        var agent = new { id, name = id, description = id, temperature = 0.0f,
            systemPrompt = id == "planner" ? Scenarios.SystemPrompt(scenario) :
                "Read the specified marker with mcli_fs_read and return its exact value. Use real tools." };
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "agents", id + ".json"), JsonSerializer.Serialize(agent));
    }
    var prompt = Scenarios.Prepare(scenario, workspace);
    var start = new ProcessStartInfo(cli) { WorkingDirectory = workspace, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.Environment["USERPROFILE"] = home;
    start.Environment["HOME"] = home;
    start.Environment["MADORIN_CONFIG_DIR"] = configDirectory;
    foreach (var argument in new[] { "run", "--mode", "expert", "--agent", "planner", "--input", prompt,
        "--timeout", "600", "--no-stream", "--output-format", "json", "--allow-tools",
        "mcli_expert_create_plan,mcli_expert_execute_step,mcli_expert_get_plan,mcli_expert_update_step_status," +
        "mcli_delegate_agent,mcli_agent_job_create_many,mcli_agent_job_wait,mcli_agent_job_result," +
        "mcli_fs_write,mcli_fs_read,mcli_fs_inspect,mcli_fs_list" })
    {
        start.ArgumentList.Add(argument);
    }
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start CLI.");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(11));
    try
    {
        await process.WaitForExitAsync(timeout.Token);
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
    exit = process.ExitCode;
    await File.WriteAllTextAsync(Path.Combine(output, "cli-output.json"), await stdoutTask);
    await File.WriteAllTextAsync(Path.Combine(output, "cli-error.txt"), await stderrTask);
}
finally
{
    File.Delete(configCopy);
    Console.WriteLine("CREDENTIAL_COPY|deleted");
}
var passed = await Evidence.VerifyAsync(scenario, workspace);
Console.WriteLine($"EXIT_CODE|{exit}");
Console.WriteLine(passed && exit == 0 ? "RESULT|PASS" : "RESULT|FAIL");
return passed && exit == 0 ? 0 : 1;
