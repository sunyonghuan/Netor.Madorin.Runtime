using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Maps Runtime tool descriptors to provider-safe function names.</summary>
public static class ProviderToolNamePolicy
{
    public const int MaximumNameLength = 64;

    /// <summary>Returns the stable function name exposed to a model Provider.</summary>
    public static string GetName(ToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return IsProviderSafe(tool.DisplayName)
            ? tool.DisplayName
            : EncodeToolId(tool.ToolId);
    }

    /// <summary>Returns a safe function name for a canonical history tool call.</summary>
    public static string GetHistoryName(
        IReadOnlyList<ToolDescriptor>? tools,
        string toolId,
        string storedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(storedName);

        if (tools is not null)
        {
            foreach (var tool in tools)
            {
                if (string.Equals(tool.ToolId, toolId, StringComparison.Ordinal))
                {
                    return GetName(tool);
                }
            }
        }

        return IsProviderSafe(storedName)
            ? storedName
            : EncodeToolId(toolId);
    }

    /// <summary>Resolves a Provider function name back to the canonical Runtime tool ID.</summary>
    public static string ResolveToolId(
        IReadOnlyList<ToolDescriptor>? tools,
        string providerName)
    {
        ArgumentNullException.ThrowIfNull(providerName);

        if (tools is null)
        {
            return providerName;
        }

        foreach (var tool in tools)
        {
            if (string.Equals(GetName(tool), providerName, StringComparison.Ordinal))
            {
                return tool.ToolId;
            }
        }

        // Accept names persisted by Runtime versions that exposed DisplayName directly.
        foreach (var tool in tools)
        {
            if (string.Equals(tool.DisplayName, providerName, StringComparison.Ordinal)
                || string.Equals(tool.ToolId, providerName, StringComparison.Ordinal))
            {
                return tool.ToolId;
            }
        }

        return providerName;
    }

    /// <summary>Rejects a request whose descriptors would expose duplicate function names.</summary>
    public static void ValidateUniqueNames(IReadOnlyList<ToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var name = GetName(tool);
            if (!names.Add(name))
            {
                throw new InvalidOperationException(
                    $"Provider tool name '{name}' is duplicated in the current tool catalog.");
            }
        }
    }

    private static bool IsProviderSafe(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength)
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static string EncodeToolId(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);

        var builder = new StringBuilder(toolId.Length + 8);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in toolId.EnumerateRunes())
        {
            if (rune.IsAscii && IsAsciiLetterOrDigit((char)rune.Value))
            {
                builder.Append((char)rune.Value);
            }
            else if (rune.Value == '-')
            {
                builder.Append('-');
            }
            else if (rune.Value == '_')
            {
                builder.Append('_');
            }
            else if (rune.Value == '.')
            {
                builder.Append("__");
            }
            else
            {
                var byteCount = rune.EncodeToUtf8(bytes);
                for (var index = 0; index < byteCount; index++)
                {
                    builder.Append("_x");
                    builder.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
                }
            }
        }

        if (builder.Length <= MaximumNameLength)
        {
            return builder.ToString();
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(toolId)));
        const int hashLength = 12;
        var prefixLength = MaximumNameLength - hashLength - 2;
        return string.Concat(builder.ToString(0, prefixLength), "__", hash.AsSpan(0, hashLength));
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9';
}
