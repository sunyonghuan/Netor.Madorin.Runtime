using System.Collections.Frozen;

namespace Madorin.AI.Runtime.Tools.Abstractions;

/// <summary>An immutable merged view of built-in and host-owned tools.</summary>
public sealed class ToolCatalogSnapshot
{
    private readonly FrozenDictionary<string, ToolDescriptor> _tools;
    private readonly IReadOnlyList<ToolDescriptor> _allTools;
    private readonly IReadOnlyList<ToolDescriptor> _orderedTools;

    public ToolCatalogSnapshot(
        string hostCatalogVersion,
        string effectiveVersion,
        IEnumerable<ToolDescriptor> tools)
    {
        HostCatalogVersion = hostCatalogVersion;
        EffectiveVersion = effectiveVersion;
        var copies = tools
            .Select(CloneDescriptor)
            .OrderBy(static tool => tool.ToolId, StringComparer.Ordinal)
            .ToArray();
        _tools = copies.ToFrozenDictionary(static tool => tool.ToolId, StringComparer.Ordinal);
        _allTools = Array.AsReadOnly(copies);
        _orderedTools = Array.AsReadOnly(copies
            .Where(static tool => tool.Tags?.Contains("internal", StringComparer.Ordinal) != true)
            .ToArray());
    }

    /// <summary>Gets the version assigned by the host to its catalog partition.</summary>
    public string HostCatalogVersion { get; }

    /// <summary>Gets the SHA-256 version of the normalized merged catalog.</summary>
    public string EffectiveVersion { get; }

    /// <summary>Gets all tools in ordinal tool-id order.</summary>
    public IReadOnlyList<ToolDescriptor> Tools => _orderedTools;

    /// <summary>Gets all descriptors, including Runtime-internal descriptors used only by the Gateway.</summary>
    public IReadOnlyList<ToolDescriptor> AllTools => _allTools;

    /// <summary>Creates a fixed snapshot with additional internal descriptors hidden from Providers.</summary>
    public ToolCatalogSnapshot WithInternalTools(IEnumerable<ToolDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        return new ToolCatalogSnapshot(
            HostCatalogVersion,
            EffectiveVersion,
            _allTools.Concat(descriptors));
    }

    /// <summary>Looks up a tool within this fixed snapshot.</summary>
    public bool TryGetTool(string toolId, out ToolDescriptor? descriptor) =>
        _tools.TryGetValue(toolId, out descriptor);

    private static ToolDescriptor CloneDescriptor(ToolDescriptor descriptor) =>
        descriptor with
        {
            Tags = descriptor.Tags is null
                ? Array.Empty<string>()
                : Array.AsReadOnly(descriptor.Tags.ToArray()),
            Capabilities = descriptor.Capabilities is null
                ? Array.Empty<string>()
                : Array.AsReadOnly(descriptor.Capabilities.ToArray())
        };
}
