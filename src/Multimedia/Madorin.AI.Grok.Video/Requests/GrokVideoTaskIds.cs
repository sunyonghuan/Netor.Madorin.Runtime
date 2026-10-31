namespace Madorin.AI.Grok.Video.Requests;

internal static class GrokVideoTaskIds
{
    public static string? Find(string[] args)
    {
        string? value = null;
        var count = 0;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--task-id" && index + 1 < args.Length)
            {
                value = args[++index];
                count++;
            }
            else if (args[index].StartsWith("--task-id=", StringComparison.Ordinal))
            {
                value = args[index][10..];
                count++;
            }
        }
        return count == 1 && IsValid(value) ? value : null;
    }

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") && !value.Any(char.IsControl);
}
