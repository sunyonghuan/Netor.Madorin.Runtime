using System.Globalization;

namespace Madorin.AI.Runtime.Cli.Commands;

/// <summary>把 token 数量显示成 1.7k、0.9m 这种单位。</summary>
internal static class TokenCountFormat
{
    public static string Format(long count)
    {
        var absolute = Math.Abs(count);
        if (absolute < 1_000)
        {
            return count.ToString(CultureInfo.InvariantCulture);
        }

        var scaled = absolute < 1_000_000
            ? absolute / 1_000d
            : absolute / 1_000_000d;
        var suffix = absolute < 1_000_000 ? "k" : "m";
        if (suffix == "k" && scaled >= 999.95d)
        {
            scaled = absolute / 1_000_000d;
            suffix = "m";
        }

        var text = scaled.ToString("0.#", CultureInfo.InvariantCulture);
        return count < 0 ? "-" + text + suffix : text + suffix;
    }
}
