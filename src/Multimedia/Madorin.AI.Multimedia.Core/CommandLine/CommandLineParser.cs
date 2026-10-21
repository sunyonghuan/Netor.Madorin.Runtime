using System.Globalization;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Multimedia.Core.CommandLine;

public static class CommandLineParser
{
    public static Dictionary<string, string?> Parse(string[] args, IReadOnlySet<string> allowed, IReadOnlySet<string> switches)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var raw = args[index];
            if (!raw.StartsWith("--", StringComparison.Ordinal))
            {
                throw new SkillException("invalid_parameter", "不接受位置参数。");
            }

            var parts = raw[2..].Split('=', 2);
            var name = parts[0];
            if (!allowed.Contains(name) || result.ContainsKey(name))
            {
                throw new SkillException("invalid_parameter", "选项未知或重复。");
            }

            string? value;
            if (switches.Contains(name))
            {
                value = parts.Length == 2 ? parts[1] : "true";
                if (value is not ("true" or "false"))
                {
                    throw new SkillException("invalid_parameter", "布尔开关只接受 true 或 false。");
                }
            }
            else if (parts.Length == 2)
            {
                value = parts[1];
            }
            else if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }
            else
            {
                throw new SkillException("invalid_parameter", "选项缺少值。");
            }

            result.Add(name, value);
        }

        return result;
    }

    public static int ParsePositiveInt(string? value, int fallback, string name)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value.Length == 0 || value.Any(character => character is < '0' or > '9') ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            throw new SkillException("invalid_parameter", $"{name} 必须是正整数。");
        }

        return number;
    }
}
