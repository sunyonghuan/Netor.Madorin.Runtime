using System.Text;

namespace Madorin.AI.Runtime.Services.Prompt;

/// <summary>
/// Process-level toggle for loading the workspace AGENTS.md file. Standalone mode
/// starts disabled; host mode starts enabled.
/// </summary>
public sealed class WorkspaceInstructionsState
{
    private int _enabled;

    public WorkspaceInstructionsState(bool enabled = false) =>
        _enabled = enabled ? 1 : 0;

    public bool Enabled => Volatile.Read(ref _enabled) != 0;

    public void SetEnabled(bool enabled) =>
        Volatile.Write(ref _enabled, enabled ? 1 : 0);
}

/// <summary>
/// Appends the workspace AGENTS.md content to a system prompt between the agent
/// prompt and the memory/environment sections.
/// </summary>
public static class WorkspaceInstructionsPrompt
{
    private const string Heading = "## Workspace Instructions";

    public static string Append(string instructions, WorkspaceInstructions? workspaceInstructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        if (workspaceInstructions is null || string.IsNullOrWhiteSpace(workspaceInstructions.Content))
        {
            return instructions;
        }

        var builder = new StringBuilder(instructions.Length + workspaceInstructions.Content.Length + 96);
        if (instructions.Length > 0)
        {
            builder.Append(instructions);
            builder.Append('\n');
            builder.Append('\n');
        }

        builder.Append(Heading);
        builder.Append('\n');
        builder.Append('\n');
        builder.Append("Source: ");
        builder.Append(WorkspaceInstructionsLoader.FileName);
        builder.Append('\n');
        builder.Append('\n');
        builder.Append(workspaceInstructions.Content);
        return builder.ToString();
    }
}
