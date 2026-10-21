using System.Text.Json.Nodes;
using Madorin.AI.OpenAI.Image.Cli;

namespace Madorin.AI.Multimedia.Tests;

[TestClass]
public sealed class ModelSelectionTests
{
    [TestMethod]
    [DataRow(null, null, "gpt-image-1")]
    [DataRow("gpt-image-1-mini", null, "gpt-image-1-mini")]
    [DataRow("gpt-image-1.5", "gpt-image-1-mini", "gpt-image-1-mini")]
    [DataRow("other-cli-model", "gpt-image-1.5", "gpt-image-1.5")]
    public async Task ModelSelectionUsesDirectThenFileThenDeclaredDefault(string? fileModel, string? directModel, string expected)
    {
        using var workspace = new TestWorkspace();
        var content = JsonNode.Parse("""{"schema_version":"1.0","task":"text-to-image","prompt":"hello"}""")!.AsObject();
        if (fileModel is not null) { content["model"] = fileModel; }
        var requestPath = Path.Combine(workspace.Root, "request.json");
        var original = content.ToJsonString();
        await File.WriteAllTextAsync(requestPath, original);
        string[] modelArgs = directModel is null ? [] : ["--model", directModel];

        var response = await Invoke(workspace, ["generate", "--request-file", "request.json", "--request-id", "selection",
            "--api-key", "placeholder", "--try-run", .. modelArgs]);

        Assert.AreEqual("validated", response["status"]!.GetValue<string>());
        Assert.AreEqual(expected, response["model"]!.GetValue<string>());
        Assert.AreEqual(expected, response["effective_parameters"]!["model"]!.GetValue<string>());
        Assert.AreEqual(expected, response["request_preview"]!["model"]!.GetValue<string>());
        if (directModel is null && fileModel is null)
        {
            Assert.IsNull(response["input_parameters"]!["model"]);
        }
        else
        {
            Assert.AreEqual(expected, response["input_parameters"]!["model"]!.GetValue<string>());
        }
        Assert.HasCount(0, response["ignored_parameters"]!.AsArray());
        Assert.AreEqual(original, await File.ReadAllTextAsync(requestPath));
    }

    [TestMethod]
    public async Task SameContentFileAcceptsIndependentModelsIdsAndOutputDirectories()
    {
        using var workspace = new TestWorkspace();
        var requestPath = Path.Combine(workspace.Root, "request.json");
        const string original = """{"schema_version":"1.0","task":"text-to-image","prompt":"hello","parameters":{"size":"1024x1024"}}""";
        await File.WriteAllTextAsync(requestPath, original);

        foreach (var model in new[] { "gpt-image-1-mini", "gpt-image-1.5" })
        {
            var id = "request-" + model;
            var outputDirectory = "artifacts/" + model;
            var response = await Invoke(workspace, ["generate", "--request-file", "request.json", "--output-dir", outputDirectory,
                "--api-url", "https://api.openai.com/v1", "--api-key", "placeholder", "--model", model, "--request-id", id, "--try-run"]);

            Assert.AreEqual("validated", response["status"]!.GetValue<string>());
            Assert.AreEqual(id, response["response_id"]!.GetValue<string>());
            Assert.AreEqual(model, response["request_preview"]!["model"]!.GetValue<string>());
            Assert.AreEqual(outputDirectory, response["effective_parameters"]!["execution"]!["output_dir"]!.GetValue<string>());
            Assert.AreEqual("<redacted>", response["input_parameters"]!["api_key"]!.GetValue<string>());
            Assert.AreEqual(original, await File.ReadAllTextAsync(requestPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "artifacts")));
            foreach (var sideEffect in response["side_effects"]!.AsObject())
            {
                Assert.IsFalse(sideEffect.Value!.GetValue<bool>());
            }
        }
    }

    [TestMethod]
    [DataRow("other-cli-model", null, "text-to-image")]
    [DataRow("gpt-image-1", "other-cli-model", "text-to-image")]
    [DataRow(null, "", "text-to-image")]
    [DataRow(null, "gpt-image-1-mini", "image-edit")]
    public async Task InvalidSelectedModelOrTaskDoesNotFallBack(string? fileModel, string? directModel, string task)
    {
        using var workspace = new TestWorkspace();
        var content = JsonNode.Parse("""{"schema_version":"1.0","prompt":"hello"}""")!.AsObject();
        content["task"] = task;
        if (fileModel is not null) { content["model"] = fileModel; }
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "request.json"), content.ToJsonString());
        string[] modelArgs = directModel is null ? [] : ["--model", directModel];

        var response = await Invoke(workspace, ["generate", "--request-file", "request.json", "--request-id", "invalid-model",
            "--api-key", "placeholder", "--try-run", .. modelArgs], 1);

        Assert.AreEqual("invalid", response["status"]!.GetValue<string>());
        Assert.AreEqual("invalid-model", response["response_id"]!.GetValue<string>());
        Assert.AreEqual("invalid_parameter", response["errors"]![0]!["code"]!.GetValue<string>());
        Assert.IsNull(response["request_preview"]);
        Assert.HasCount(0, response["ignored_parameters"]!.AsArray());
    }

    private static async Task<JsonNode> Invoke(TestWorkspace workspace, string[] args, int expectedExitCode = 0)
    {
        using var client = new HttpClient(new NoNetwork());
        using var output = new StringWriter();
        var exitCode = await new OpenAiImageApplication(client).RunAsync(args, output, workspace.Root);
        Assert.AreEqual(expectedExitCode, exitCode);
        return JsonNode.Parse(output.ToString())!;
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Model selection must not contact a provider.");
    }
}
