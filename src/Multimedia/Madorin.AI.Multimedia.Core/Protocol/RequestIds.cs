namespace Madorin.AI.Multimedia.Core.Protocol;

public static class RequestIds
{
    public static string? FindRequestId(string[] args)
    {
        string? requestId = null;
        var count = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--request-id" && i + 1 < args.Length)
            {
                count++;
                requestId = args[++i];
            }
            else if (args[i].StartsWith("--request-id=", StringComparison.Ordinal))
            {
                count++;
                requestId = args[i][13..];
            }
        }
        return count == 1 ? requestId : null;
    }

    public static bool IsValidRequestId(string? value) => value is { Length: >= 1 and <= 128 } &&
        ((value[0] is >= 'A' and <= 'Z') || (value[0] is >= 'a' and <= 'z') || (value[0] is >= '0' and <= '9')) &&
        value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or ':' or '-');
}
