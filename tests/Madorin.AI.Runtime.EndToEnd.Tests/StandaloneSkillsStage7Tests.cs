using System.Text.Json;
using Madorin.AI.Runtime.Cli;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneSkillsStage7Tests
{
    [TestMethod]
    public void SkillCommands_WorkspaceScope_ShowAndValidateWithoutExecutingScript()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "data");
            var marker = Path.Combine(root, "script-executed.txt");
            WriteSkill(workspace, marker);

            var list = Run(
                "skill", "list", "--scope", "workspace",
                "--workspace", workspace, "--data-dir", dataDirectory, "--json");
            Assert.AreEqual(ExitCodes.Success, list.ExitCode, list.Error);
            using (var document = JsonDocument.Parse(list.Output))
            {
                var data = document.RootElement.GetProperty("data");
                Assert.AreEqual(1, data.GetProperty("skillCount").GetInt32());
                var skill = Assert.ContainsSingle(data
                    .GetProperty("skills")
                    .EnumerateArray()
                    .ToArray());
                Assert.AreEqual("workspace-demo", skill.GetProperty("name").GetString());
                Assert.AreEqual("workspace", skill.GetProperty("sourceId").GetString());
            }

            var show = Run(
                "skill", "show", "workspace-demo", "--scope", "workspace",
                "--workspace", workspace, "--data-dir", dataDirectory, "--json");
            Assert.AreEqual(ExitCodes.Success, show.ExitCode, show.Error);
            using (var document = JsonDocument.Parse(show.Output))
            {
                var data = document.RootElement.GetProperty("data");
                Assert.AreEqual("workspace-demo", data.GetProperty("name").GetString());
                Assert.Contains("allowed-tools", data.GetProperty("frontmatter").GetString()!);
                Assert.Contains("Workspace demo body", data.GetProperty("body").GetString()!);
                Assert.AreEqual(
                    "scripts/validate.js",
                    Assert.ContainsSingle(data.GetProperty("scripts").EnumerateArray().ToArray())
                        .GetString());
            }

            var validate = Run(
                "skill", "validate", "--scope", "workspace",
                "--workspace", workspace, "--data-dir", dataDirectory, "--json");
            Assert.AreEqual(ExitCodes.Success, validate.ExitCode, validate.Error);
            Assert.IsFalse(File.Exists(marker), "skill validate must not execute scripts");
            using var validationDocument = JsonDocument.Parse(validate.Output);
            Assert.IsTrue(validationDocument.RootElement
                .GetProperty("data")
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Any(static diagnostic =>
                    diagnostic.GetProperty("code").GetString() == "SkillToolUnavailable"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static CliResult Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliApplication.Run(args, output, TextReader.Null, error);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private static void WriteSkill(string workspace, string marker)
    {
        var directory = Path.Combine(workspace, ".madorin", "skills", "workspace-demo");
        Directory.CreateDirectory(Path.Combine(directory, "resources"));
        Directory.CreateDirectory(Path.Combine(directory, "scripts"));
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            "---\n"
            + "name: workspace-demo\n"
            + "description: Workspace Skill\n"
            + "allowed-tools:\n"
            + "  - mcli_memory_read\n"
            + "  - host.missing\n"
            + "---\n"
            + "Workspace demo body.\n");
        File.WriteAllText(Path.Combine(directory, "resources", "guide.md"), "Guide\n");
        var escapedMarker = marker
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        File.WriteAllText(
            Path.Combine(directory, "scripts", "validate.js"),
            $"require('fs').writeFileSync('{escapedMarker}', 'executed');\n");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-skills-stage7-tests",
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

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
