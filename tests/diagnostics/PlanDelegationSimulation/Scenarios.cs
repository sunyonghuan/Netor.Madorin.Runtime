internal static class Scenarios
{
    private static string ReadTask(string workspace, char area)
    {
        var file = Path.Combine(workspace, "temp-output", $"area-{area}", $"marker-{area}.txt");
        return $"Use mcli_fs_read on the existing absolute file '{file}' and report its exact content marker-{area}. " +
            "Do not write files. If reading fails, report failure, not success.";
    }

    public static string SystemPrompt(string scenario) => scenario switch
    {
        "files" => "Use actual plan and file tools. Only a temporary delegated reviewer may write gamma.txt; " +
            "do not write it yourself. Verify tool results and complete both plan steps.",
        "parallel" => "Use exactly one mcli_agent_job_create_many call with three temporary read-only jobs. " +
            "Wait for all three job IDs and collect their results. No invented success.",
        _ => "Create an expert plan with exactly three steps, then invoke mcli_expert_execute_step " +
            "for indexes 0, 1, 2 in order. The executionPolicy MUST automatically invoke agents; " +
            "never manually call mcli_delegate_agent or agent_job_create_many. " +
            "Do not complete steps with update_step_status. Verify final plan with mcli_expert_get_plan."
    };

    public static string Prepare(string scenario, string workspace)
    {
        if (scenario == "files")
        {
            return "Create a two-step plan. Step 0: PrimaryAgent write temp-output/alpha.txt=alpha " +
                "and temp-output/beta.txt=beta with mcli_fs_write; set step running then execute_step index 0. " +
                "Step 1: PrimaryAgent must use mcli_delegate_agent with createIfMissing=true, temporary=true, " +
                "allowedToolIds=[mcli_fs_write], agentName=gamma-reviewer and task to write " +
                "temp-output/gamma.txt=gamma. Main agent must not write gamma.txt. " +
                "Set step running first; after delegated tool succeeds execute_step index 1. Verify files and plan completed.";
        }

        foreach (var area in "abc")
        {
            var directory = Path.Combine(workspace, "temp-output", $"area-{area}");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"marker-{area}.txt"), $"marker-{area}");
        }
        var tasks = "abc".Select(area => ReadTask(workspace, area)).ToArray();
        if (scenario == "parallel")
        {
            return "The three marker directories exist. Call mcli_agent_job_create_many ONCE with " +
                "jobs=[{agentName:search-a,task:'" + tasks[0] + "',createIfMissing:true,temporary:true," +
                "allowedToolIds:[mcli_fs_read,mcli_fs_list,mcli_fs_inspect]}," +
                "{agentName:search-b,task:'" + tasks[1] + "',createIfMissing:true,temporary:true," +
                "allowedToolIds:[mcli_fs_read,mcli_fs_list,mcli_fs_inspect]}," +
                "{agentName:search-c,task:'" + tasks[2] + "',createIfMissing:true,temporary:true," +
                "allowedToolIds:[mcli_fs_read,mcli_fs_list,mcli_fs_inspect]}]. " +
                "Call mcli_agent_job_wait with all three IDs, then mcli_agent_job_result with the same IDs.";
        }

        return "Create a plan with three steps, each completionMode=tools. " +
            "Step 0: title existing-reader, agentName existing-reader, executionPolicy ExistingAgentOnly, " +
            "description '" + tasks[0] + "'. Step 1: title temporary, agentName missing-reader, " +
            "executionPolicy TemporaryAgentIfUnmatched, description '" + tasks[1] + "'. " +
            "Step 2: title parallel, executionPolicy PreferParallelTemporaryAgents, " +
            "parallelTasks ['" + tasks[0] + "','" + tasks[1] + "','" + tasks[2] + "']. " +
            "Now call execute_step for indexes 0,1,2 in order. Get the plan; all must be completed. " +
            "Only execute_step runs workers; no manual delegation.";
    }
}
