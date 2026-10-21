using System.Net;
using System.Text.Json.Nodes;
using Json.Schema;
using Madorin.AI.OpenAI.Image.Cli;

namespace Madorin.AI.Multimedia.Tests;

[TestClass]
public sealed class RequestContractTests
{
    [TestMethod]
    public async Task MetadataSchemasAcceptEffectiveRequestWithoutConflictingDefinitions()
    {
        using var workspace = new TestWorkspace();
        var metadata = await Invoke(workspace, ["metadata", "--request-id", "meta"]);
        var preview = await Invoke(workspace, ["generate", "--request-id", "preview", "--prompt", "水墨山水", "--api-key", "placeholder", "--output-format", "json", "--provider-openai-output-format", "jpeg", "--try-run"]);
        Assert.AreEqual("validated", preview["status"]!.GetValue<string>());
        var schema = JsonSchema.FromText(metadata["models"]![0]!["tasks"]![0]!["validation_schema"]!.ToJsonString());
        using var effective = System.Text.Json.JsonDocument.Parse(preview["effective_parameters"]!.ToJsonString());
        Assert.IsTrue(schema.Evaluate(effective.RootElement).IsValid);
        var parameters = metadata["commands"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == "generate")!["parameters"]!.AsArray()
            .Concat(metadata["authentication"]!["parameters"]!.AsArray()).Concat(metadata["models"]![0]!["tasks"]![0]!["parameters"]!.AsArray()).ToArray();
        Assert.AreEqual(parameters.Length, parameters.Select(item => item!["cli_name"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count());
        var attachments = parameters.Where(item => item!["request_path"]?.GetValue<string>() == "/attachments").ToArray();
        Assert.HasCount(5, attachments);
        foreach (var attachment in attachments)
        {
            Assert.AreEqual("array", attachment!["type"]!.GetValue<string>());
            Assert.IsTrue(attachment["repeatable"]!.GetValue<bool>());
            Assert.IsNotNull(attachment["schema"]!["items"]);
        }
        Assert.AreEqual("jpeg", preview["request_preview"]!["output_format"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CommonFileDefaultsTaskAndConvertsDimensionsBeforeApplyingDefaults()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "request.json"), """
            {"schema_version":"1.0","request_id":"file","prompt":"hello","attachments":[],"parameters":{"width":1024,"height":1024},"execution":{"try_run":true,"output_format":"json"}}
            """);
        var response = await Invoke(workspace, ["generate", "--request-file", "request.json", "--api-key", "placeholder"]);
        Assert.AreEqual("validated", response["status"]!.GetValue<string>());
        Assert.AreEqual("1024x1024", response["request_preview"]!["size"]!.GetValue<string>());
        Assert.AreEqual("dimensions_to_size", response["converted_parameters"]![0]!["rule_id"]!.GetValue<string>());
        Assert.AreEqual(0, response["ignored_parameters"]!.AsArray().Count);
        Assert.AreEqual(1024, response["input_parameters"]!["parameters"]!["width"]!.GetValue<int>());
        Assert.IsNull(response["effective_parameters"]!["parameters"]!["width"]);
    }

    [TestMethod]
    [DataRow("{\"width\":1024}")]
    [DataRow("{\"width\":1024,\"height\":1024,\"size\":\"1536x1024\"}")]
    [DataRow("{\"width\":1024,\"height\":\"1024\"}")]
    [DataRow("{\"num_images\":\"two\"}")]
    public async Task FileOnlyTryRunReportsInvalidWithCorrelation(string parameters)
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "request.json"), "{\"schema_version\":\"1.0\",\"request_id\":\"file\",\"prompt\":\"hello\",\"execution\":{\"try_run\":true},\"parameters\":" + parameters + "}");
        var response = await Invoke(workspace, ["generate", "--request-file", "request.json", "--api-key", "placeholder"]);
        Assert.AreEqual("invalid", response["status"]!.GetValue<string>());
        Assert.AreEqual("try_run", response["mode"]!.GetValue<string>());
        Assert.AreEqual("file", response["response_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task AttachmentFilteringStillRejectsTraversal()
    {
        using var workspace = new TestWorkspace();
        foreach (var path in new[] { "missing.png", "../outside.png" })
        {
            var attachment = "{\"id\":\"source\",\"role\":\"reference\",\"source\":{\"path\":\"" + path + "\"}}";
            var response = await Invoke(workspace, ["generate", "--request-id", "attachments", "--prompt", "hello", "--api-key", "placeholder", "--attachment", attachment, "--try-run"]);
            Assert.AreEqual(path == "missing.png" ? "validated" : "invalid", response["status"]!.GetValue<string>());
            if (path == "missing.png") { Assert.AreEqual("unsupported_attachment_role", response["ignored_parameters"]![0]!["reason"]!.GetValue<string>()); }
        }
    }

    [TestMethod]
    public async Task DuplicateAttachmentIdsAndMixedAttachmentEntrypointsAreRejected()
    {
        using var workspace = new TestWorkspace();
        const string attachment = "{\"id\":\"same\",\"role\":\"reference\",\"source\":{\"path\":\"missing.png\"}}";
        foreach (var tail in new[] { new[] { "--attachment", attachment }, new[] { "--reference-image", "missing.png" } })
        {
            var response = await Invoke(workspace, ["generate", "--request-id", "duplicate", "--prompt", "hello", "--api-key", "placeholder", "--try-run", "--attachment", attachment, .. tail]);
            Assert.AreEqual("invalid", response["status"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task OldImageFormatIsConvertedWithoutChangingProtocolOutput()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "request.json"), """
            {"schema_version":"1.0","request_id":"format","prompt":"hello","parameters":{"output_format":"webp"},"execution":{"try_run":true,"output_format":"json"}}
            """);
        var response = await Invoke(workspace, ["generate", "--request-file", "request.json", "--api-key", "placeholder"]);
        Assert.AreEqual("validated", response["status"]!.GetValue<string>());
        Assert.AreEqual("webp", response["request_preview"]!["output_format"]!.GetValue<string>());
        Assert.AreEqual("image_format_to_openai", response["converted_parameters"]![0]!["rule_id"]!.GetValue<string>());
        Assert.AreEqual("json", response["effective_parameters"]!["execution"]!["output_format"]!.GetValue<string>());
        var conflict = await Invoke(workspace, ["generate", "--request-file", "request.json", "--api-key", "placeholder", "--provider-openai-output-format", "png"]);
        Assert.AreEqual("invalid", conflict["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ExplicitFalseOverridesFileTryRunEvenWhenFileStructureFails()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "request.json"), """
            {"schema_version":"1.0","request_id":"mode","unexpected":true,"execution":{"try_run":true}}
            """);
        var response = await Invoke(workspace, ["generate", "--request-file", "request.json", "--try-run=false", "--api-key", "placeholder"]);
        Assert.AreEqual("failed", response["status"]!.GetValue<string>());
        Assert.IsNull(response["mode"]);
    }

    [TestMethod]
    public async Task DirectOverridesFileBusinessValuesBeforeValidation()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "request.json"), """
            {"schema_version":"1.0","request_id":"file","prompt":42,"parameters":{"quality":null},"execution":{"try_run":true}}
            """);
        var response = await Invoke(workspace, ["generate", "--request-file", "request.json", "--api-key", "placeholder", "--prompt", "hello", "--quality", "low"]);
        Assert.AreEqual("validated", response["status"]!.GetValue<string>());
    }

    private static async Task<JsonNode> Invoke(TestWorkspace workspace, string[] args)
    {
        using var client = new HttpClient(new NoNetwork());
        using var output = new StringWriter();
        await new OpenAiImageApplication(client).RunAsync(args, output, workspace.Root);
        return JsonNode.Parse(output.ToString())!;
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Local validation must not contact a provider.");
    }
}
