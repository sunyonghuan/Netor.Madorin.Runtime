using System.Text;
using Madorin.AI.Runtime.Services.Prompt;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class WorkspaceInstructionsTests
{
    [TestMethod]
    public async Task LoadAsync_MissingFile_ReturnsNull()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var result = await WorkspaceInstructionsLoader.LoadAsync(root);

            Assert.IsNull(result);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_ExactName_ReadsUtf8Content()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var content = "# Rules\n\n- Keep it small.\n";
            await File.WriteAllTextAsync(
                Path.Combine(root, "AGENTS.md"),
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await WorkspaceInstructionsLoader.LoadAsync(root);

            Assert.IsNotNull(result);
            Assert.AreEqual(content, result.Content);
            Assert.AreEqual(
                Path.Combine(Path.GetFullPath(root), "AGENTS.md"),
                result.Path);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow("agents.md")]
    [DataRow("Agents.Md")]
    [DataRow("AGENTS.MD")]
    public async Task LoadAsync_IsCaseInsensitiveForFileName(string fileName)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, fileName),
                "case-insensitive",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await WorkspaceInstructionsLoader.LoadAsync(root);

            Assert.IsNotNull(result);
            Assert.AreEqual("case-insensitive", result.Content);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WhitespaceOnlyContent_ReturnsNull()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "AGENTS.md"),
                "   \n\t\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await WorkspaceInstructionsLoader.LoadAsync(root);

            Assert.IsNull(result);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadAsync_OversizedFile_Throws()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var oversized = new string('x', (int)WorkspaceInstructionsLoader.MaximumFileBytes + 1);
            await File.WriteAllTextAsync(
                Path.Combine(root, "AGENTS.md"),
                oversized,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => WorkspaceInstructionsLoader.LoadAsync(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Append_PlacesInstructionsAfterAgentPromptWithSource()
    {
        var instructions = new WorkspaceInstructions(
            Path.Combine("workspace", "AGENTS.md"),
            "# Rules\n\n- Keep it small.");

        var result = WorkspaceInstructionsPrompt.Append("Agent prompt.", instructions);

        var agentIndex = result.IndexOf("Agent prompt.", StringComparison.Ordinal);
        var headingIndex = result.IndexOf("## Workspace Instructions", StringComparison.Ordinal);
        var sourceIndex = result.IndexOf("Source: AGENTS.md", StringComparison.Ordinal);
        var contentIndex = result.IndexOf("- Keep it small.", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, agentIndex);
        Assert.IsGreaterThan(agentIndex, headingIndex);
        Assert.IsGreaterThan(headingIndex, sourceIndex);
        Assert.IsGreaterThan(sourceIndex, contentIndex);
    }

    [TestMethod]
    public void Append_WithoutInstructions_ReturnsAgentPrompt()
    {
        var result = WorkspaceInstructionsPrompt.Append("Agent prompt.", null);

        Assert.AreEqual("Agent prompt.", result);
    }

    [TestMethod]
    public void State_DefaultsAndToggling()
    {
        var disabled = new WorkspaceInstructionsState();
        Assert.IsFalse(disabled.Enabled);

        disabled.SetEnabled(true);
        Assert.IsTrue(disabled.Enabled);

        var enabled = new WorkspaceInstructionsState(enabled: true);
        Assert.IsTrue(enabled.Enabled);

        enabled.SetEnabled(false);
        Assert.IsFalse(enabled.Enabled);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-workspace-instructions-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
