using Madorin.AI.Runtime.Contracts.Expert;

namespace Madorin.AI.Runtime.Modes.Expert;

internal static class ExpertExecutionEvidence
{
    public static string? Validate(string mode, string? expected, string? output,
        IReadOnlyList<ExpertToolEvidence> evidence)
    {
        if (evidence.Any(static item => !item.Success))
        {
            return "A tool failed; subsequent text cannot overwrite failed execution evidence.";
        }

        return mode switch
        {
            "tools" when evidence.Count > 0 => null,
            "tools" => "No successful tool execution evidence for this step.",
            "analysis" when !string.IsNullOrWhiteSpace(expected)
                && string.Equals(output?.Trim(), expected.Trim(), StringComparison.Ordinal) => null,
            "analysis" => "Analysis result must exactly match the declared expectedOutput.",
            _ => "Unknown completion mode."
        };
    }
}
