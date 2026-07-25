using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderToolNamePolicyTests
{
    [TestMethod]
    public void GetName_BuiltinTools_ProducesProviderSafeNames()
    {
        var tools = new BuiltinToolRegistry().GetTools();

        ProviderToolNamePolicy.ValidateUniqueNames(tools);

        foreach (var tool in tools)
        {
            var name = ProviderToolNamePolicy.GetName(tool);
            AssertProviderSafe(name);
        }

        var fileRead = tools.Single(
            static tool => tool.ToolId == BuiltinToolRegistry.FileReadToolId);
        Assert.AreEqual("builtin__fs__read", ProviderToolNamePolicy.GetName(fileRead));
    }

    [TestMethod]
    public void GetName_AlreadyValidDisplayName_PreservesCompatibility()
    {
        var tool = CreateTool("host.weather", "get_weather");

        Assert.AreEqual("get_weather", ProviderToolNamePolicy.GetName(tool));
    }

    [TestMethod]
    public void GetHistoryName_InvalidStoredName_UsesCurrentDescriptorName()
    {
        var tool = CreateTool("builtin.fs.read", "Read file");

        var name = ProviderToolNamePolicy.GetHistoryName(
            [tool],
            tool.ToolId,
            tool.DisplayName);

        Assert.AreEqual("builtin__fs__read", name);
    }

    [TestMethod]
    public void ResolveToolId_AcceptsProviderAndLegacyNames()
    {
        var tool = CreateTool("builtin.fs.read", "Read file");
        ToolDescriptor[] tools = [tool];

        Assert.AreEqual(
            tool.ToolId,
            ProviderToolNamePolicy.ResolveToolId(tools, "builtin__fs__read"));
        Assert.AreEqual(
            tool.ToolId,
            ProviderToolNamePolicy.ResolveToolId(tools, "Read file"));
    }

    [TestMethod]
    public void GetName_LongToolId_IsStableAndBounded()
    {
        var tool = CreateTool($"mcp.server.{new string('a', 100)}", "读取远程内容");

        var first = ProviderToolNamePolicy.GetName(tool);
        var second = ProviderToolNamePolicy.GetName(tool);

        Assert.AreEqual(first, second);
        Assert.AreEqual(ProviderToolNamePolicy.MaximumNameLength, first.Length);
        AssertProviderSafe(first);
    }

    [TestMethod]
    public void ValidateUniqueNames_DuplicateProviderNames_Throws()
    {
        ToolDescriptor[] tools =
        [
            CreateTool("host.first", "duplicate_name"),
            CreateTool("host.second", "duplicate_name")
        ];

        Assert.ThrowsExactly<InvalidOperationException>(
            () => ProviderToolNamePolicy.ValidateUniqueNames(tools));
    }

    private static ToolDescriptor CreateTool(string toolId, string displayName) =>
        new(
            toolId,
            "test",
            displayName,
            "Test tool.",
            "{\"type\":\"object\"}");

    private static void AssertProviderSafe(string name)
    {
        Assert.IsTrue(name.Length is >= 1 and <= ProviderToolNamePolicy.MaximumNameLength);
        Assert.IsTrue(name.All(static character =>
            character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_'
            or '-'));
    }
}
