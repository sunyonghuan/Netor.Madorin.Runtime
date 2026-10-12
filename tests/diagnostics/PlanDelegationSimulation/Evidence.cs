using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class Evidence
{
    private sealed record Intent(string Agent, string Parent, string Tool, string Call, string Status,
        string Result, string Grant, string GrantAgent, string AllowedCalls, string WriteRoots);
    private sealed record ToolEvent(string Id, string Tool, bool Success, JsonElement Result);

    public static async Task<bool> VerifyAsync(string scenario, string workspace)
    {
        var messages = Path.Combine(workspace, ".madorin", "messages");
        var calls = new List<string>();
        var events = new List<ToolEvent>();
        if (Directory.Exists(messages))
        {
            foreach (var path in Directory.GetFiles(messages, "*.jsonl"))
            {
                foreach (var line in await File.ReadAllLinesAsync(path))
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("content", out var content))
                    {
                        continue;
                    }

                    foreach (var block in content.EnumerateArray())
                {
                    var type = Get(block, "type");
                    if (type == "tool_call")
                        {
                            calls.Add(Get(block, "toolId"));
                        }

                        if (type != "tool_result")
                        {
                            continue;
                        }

                        var text = block.GetProperty("content").EnumerateArray().FirstOrDefault();
                    if (text.ValueKind != JsonValueKind.Object || Get(text, "type") != "text")
                        {
                            continue;
                        }

                        try
                    {
                        using var result = JsonDocument.Parse(Get(text, "text"));
                        events.Add(new ToolEvent(Get(block, "callId"), Get(block, "toolId"),
                            block.GetProperty("success").GetBoolean(), result.RootElement.Clone()));
                    }
                    catch (JsonException) { }
                }
            }
            }
        }
        var intents = await ReadIntentsAsync(Path.Combine(workspace, ".madorin", "state.db"));
        var markers = "abc".All(area => File.Exists(Path.Combine(workspace, "temp-output",
            $"area-{area}", $"marker-{area}.txt")) &&
            File.ReadAllText(Path.Combine(workspace, "temp-output", $"area-{area}",
                $"marker-{area}.txt")) == $"marker-{area}");
        bool pass;
        if (scenario == "parallel")
        {
            pass = markers && VerifyParallel(events, intents, calls);
        }
        else if (scenario == "policy")
        {
            pass = markers && !calls.Contains("mcli_delegate_agent") &&
                !calls.Contains("mcli_agent_job_create_many") &&
                events.Count(e => e.Tool == "mcli_expert_execute_step" && e.Success) == 3 &&
                VerifyPolicyPlan(workspace, intents);
        }
        else
        {
            var files = new[] { "alpha", "beta", "gamma" };
            pass = files.All(name => File.Exists(Path.Combine(workspace, "temp-output", name + ".txt")) &&
                File.ReadAllText(Path.Combine(workspace, "temp-output", name + ".txt")) == name) &&
                VerifyGamma(workspace, intents) && VerifyPlan(workspace, 2) &&
                events.Count(e => e.Tool == "mcli_expert_execute_step" && e.Success) == 2;
        }
        Console.WriteLine($"EVIDENCE|scenario={scenario}|markers={markers}|intents={intents.Count}|calls={calls.Count}|verified={pass}");
        return pass;
    }

    private static string Get(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ToString() : "";

    private static async Task<List<Intent>> ReadIntentsAsync(string database)
    {
        var intents = new List<Intent>();
        if (!File.Exists(database))
        {
            return intents;
        }

        await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.agent_id,i.parent_agent_id,i.tool_id,i.call_id,i.status,i.result_json,
                   i.grant_id,g.agent_id,g.allowed_call_ids,g.allowed_write_roots
            FROM tool_intents i LEFT JOIN tool_grants g ON g.grant_id=i.grant_id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string Field(int index) => reader.IsDBNull(index) ? "" : reader.GetString(index);
            intents.Add(new Intent(Field(0), Field(1), Field(2), Field(3), Field(4),
                Field(5), Field(6), Field(7), Field(8), Field(9)));
        }
        return intents;
    }
    private static bool VerifyParallel(List<ToolEvent> events, List<Intent> intents, List<string> calls)
    {
        var creates = events.Where(e => e.Tool == "mcli_agent_job_create_many").ToArray();
        var waits = events.Where(e => e.Tool == "mcli_agent_job_wait").ToArray();
        var results = events.Where(e => e.Tool == "mcli_agent_job_result").ToArray();
        if (calls.Count(c => c == "mcli_agent_job_create_many") != 1 || creates.Length != 1 ||
            !creates[0].Success || waits.Length == 0 || results.Length == 0)
        {
            return false;
        }

        var created = creates[0].Result.GetProperty("jobs").EnumerateArray().ToArray();
        if (created.Length != 3 || created.Select(e => Get(e, "jobId")).Distinct().Count() != 3)
        {
            return false;
        }

        var ids = created.Select(e => Get(e, "jobId")).ToHashSet();
        var waited = waits.LastOrDefault(e => e.Success);
        var collected = results.LastOrDefault(e => e.Success);
        if (waited is null || collected is null)
        {
            return false;
        }

        var jobs = waited.Result.GetProperty("jobs").EnumerateArray().ToArray();
        var collectedJobs = collected.Result.GetProperty("jobs").EnumerateArray().ToArray();
        if (jobs.Length != 3 || collectedJobs.Length != 3 ||
            !jobs.Select(j => Get(j, "jobId")).ToHashSet().SetEquals(ids) ||
            !collectedJobs.Select(j => Get(j, "jobId")).ToHashSet().SetEquals(ids))
        {
            return false;
        }

        var starts = new List<DateTimeOffset>();
        var ends = new List<DateTimeOffset>();
        var agents = new HashSet<string>();
        foreach (var (job, index) in jobs.Select((job, index) => (job, index)))
        {
            var marker = "marker-" + "abc"[index];
            if (Get(job, "state") != "completed" || Get(job, "success") != "True" ||
                !Get(job, "result").Contains(marker, StringComparison.Ordinal) ||
                !Get(collectedJobs.First(j => Get(j, "jobId") == Get(job, "jobId")), "result")
                    .Contains(marker, StringComparison.Ordinal))
            {
                return false;
            }

            if (!DateTimeOffset.TryParse(Get(job, "startedAt"), out var started) ||
                !DateTimeOffset.TryParse(Get(job, "completedAt"), out var completed))
            {
                return false;
            }

            starts.Add(started);
            ends.Add(completed);
            var child = job.GetProperty("childAgentIds").EnumerateArray().Select(e => e.GetString()).SingleOrDefault();
            if (child is null || !child.StartsWith("temp-", StringComparison.Ordinal) ||
                !agents.Add(child) || !ReadSucceeded(intents, child, marker))
            {
                return false;
            }
        }
        var overlapping = starts.Max() < ends.Min();
        Console.WriteLine($"PARALLEL|jobs={ids.Count}|children={agents.Count}|overlap={overlapping}|latestStart={starts.Max():O}|earliestEnd={ends.Min():O}");
        return overlapping;
    }
    private static bool ReadSucceeded(List<Intent> intents, string child, string marker) =>
        intents.Any(i => i.Agent == child && i.Parent == "planner" && i.Tool == "mcli_fs_read" &&
            i.Status == "succeeded" && i.Result.Contains(marker, StringComparison.Ordinal)) &&
        !intents.Any(i => i.Agent == child && i.Status != "succeeded");

    private static JsonElement[] PlanSteps(string workspace)
    {
        var folder = Path.Combine(workspace, ".madorin", "plans");
        if (!Directory.Exists(folder))
        {
            return [];
        }

        foreach (var file in Directory.GetFiles(folder, "*-completed.json", SearchOption.AllDirectories))
        {
            using var plan = JsonDocument.Parse(File.ReadAllText(file));
            return plan.RootElement.GetProperty("steps").EnumerateArray().Select(s => s.Clone()).ToArray();
        }
        return [];
    }

    private static bool VerifyPlan(string workspace, int count)
    {
        var steps = PlanSteps(workspace);
        return steps.Length == count && steps.All(s => Get(s, "status").Equals("Completed",
            StringComparison.OrdinalIgnoreCase) && s.GetProperty("evidence").GetArrayLength() > 0);
    }

    private static bool VerifyPolicyPlan(string workspace, List<Intent> intents)
    {
        if (!VerifyPlan(workspace, 3))
        {
            return false;
        }

        var steps = PlanSteps(workspace);
        var policies = new[] { "ExistingAgentOnly", "TemporaryAgentIfUnmatched", "PreferParallelTemporaryAgents" };
        for (var index = 0; index < 3; index++)
        {
            if (Get(steps[index], "executionPolicy") != policies[index])
            {
                return false;
            }

            var proof = steps[index].GetProperty("evidence").EnumerateArray().ToArray();
            if (proof.Any(p => Get(p, "success") != "True"))
            {
                return false;
            }

            var children = proof.Select(p => Get(p, "agentId")).Distinct().ToArray();
            if (index == 0 && (children.Length != 1 || children[0] != "existing-reader" ||
                !ReadSucceeded(intents, children[0], "marker-a")))
            {
                return false;
            }

            if (index == 1 && (children.Length != 1 || !children[0].StartsWith("temp-") ||
                !ReadSucceeded(intents, children[0], "marker-b")))
            {
                return false;
            }

            if (index == 2 && (children.Length != 3 || children.Any(c => !c.StartsWith("temp-")) ||
                !"abc".All(a => children.Any(c => ReadSucceeded(intents, c, $"marker-{a}")))))
            {
                return false;
            }

            Console.WriteLine($"POLICY|{policies[index]}|children={string.Join(',', children)}|proofs={proof.Length}");
        }
        return true;
    }
    private static bool VerifyGamma(string workspace, List<Intent> intents)
    {
        var path = Path.Combine(workspace, "temp-output", "gamma.txt");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        var writes = intents.Where(i => i.Tool == "mcli_fs_write" && i.Status == "succeeded" &&
            i.Result.Contains(hash, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (writes.Length != 1)
        {
            return false;
        }

        var write = writes[0];
        using var result = JsonDocument.Parse(write.Result);
        using var calls = JsonDocument.Parse(write.AllowedCalls);
        using var roots = JsonDocument.Parse(write.WriteRoots);
        var pass = write.Agent.StartsWith("temp-", StringComparison.Ordinal) &&
            write.Parent == "planner" && write.GrantAgent == write.Agent && write.Grant.Length > 0 &&
            Get(result.RootElement, "written") == "True" &&
            Get(result.RootElement, "byteLength") == "5" && Get(result.RootElement, "mode") == "create" &&
            calls.RootElement.EnumerateArray().Any(c => c.GetString() == write.Call) &&
            roots.RootElement.EnumerateArray().Any(r => path.StartsWith(
                Path.GetFullPath(r.GetString()!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || path.Equals(Path.GetFullPath(r.GetString()!),
                    StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"GAMMA|child={write.Agent}|call={write.Call}|grant={write.Grant}|verified={pass}");
        return pass;
    }
}

