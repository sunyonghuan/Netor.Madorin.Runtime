namespace Madorin.AI.Kling.Image.Requests;

internal static class KlingTaskIds
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
        value is { Length: > 0 and <= 128 } && value is not ("." or "..") &&
        !value.Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#' or ' ');
}
