using System.Text.Json;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class CommandEncodingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(null, "中文输入与输出\n第二行")]
    [DataRow("utf-8", "中文输入与输出\n第二行")]
    [DataRow("gbk", "中文输入与输出\n第二行")]
    [DataRow("gb18030", "中文输入与输出\n第二行")]
    [DataRow("gb18030", "中文\U0001F600\U00020000")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Execute_Encoding_RoundTripsStandardInputOutputAndError(string? encoding, string text)
    {
        var shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var script = OperatingSystem.IsWindows()
            ? "$text=[Console]::In.ReadToEnd();[Console]::Out.Write($text);[Console]::Error.Write($text)"
            : "tee /dev/stderr";
        var result = await ExecuteAsync(CreateArguments(script, shell, encoding, text));

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32(), result.OutputJson);
        Assert.AreEqual(text, output.RootElement.GetProperty("stdout").GetString());
        Assert.AreEqual(text, output.RootElement.GetProperty("stderr").GetString());
    }

    [TestMethod]
    [DataRow("utf-8", "65001")]
    [DataRow("gbk", "936")]
    [DataRow("gb18030", "54936")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Execute_CmdEncoding_ConfiguresCodePage(string encoding, string codePage)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("CMD is only available on Windows.");
            return;
        }

        var result = await ExecuteAsync(CreateArguments("chcp", "cmd", encoding));
        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32(), result.OutputJson);
        Assert.Contains(codePage, output.RootElement.GetProperty("stdout").GetString() ?? string.Empty);
    }

    [TestMethod]
    [DataRow("auto")]
    [DataRow("invalid")]
    [DataRow("")]
    public async Task Execute_UnsupportedEncoding_ReturnsToolFailure(string encoding)
    {
        var result = await ExecuteAsync(CreateArguments("exit 0", "auto", encoding));

        Assert.IsFalse(result.Success);
        Assert.Contains("encoding", result.Error ?? string.Empty);
    }

    [TestMethod]
    public void Descriptor_EncodingAndParameterDefaults_AreExplicitAndValidate()
    {
        var descriptor = new BuiltinToolRegistry().GetTools()
            .Single(tool => tool.ToolId == BuiltinToolRegistry.CommandToolId);
        using var schema = JsonDocument.Parse(descriptor.InputSchemaJson);
        var properties = schema.RootElement.GetProperty("properties");
        Assert.AreEqual("utf-8", properties.GetProperty("encoding").GetProperty("default").GetString());
        Assert.AreEqual("auto", properties.GetProperty("shell").GetProperty("default").GetString());
        foreach (var property in properties.EnumerateObject())
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(property.Value.GetProperty("description").GetString()));
        }

        var validator = new JsonSchemaToolValidator();
        using var defaults = JsonDocument.Parse("""{"script":"git status"}""");
        using var legacy = JsonDocument.Parse("""{"script":"legacy-cli","encoding":"gbk"}""");
        using var invalid = JsonDocument.Parse("""{"script":"git status","encoding":"auto"}""");
        Assert.IsTrue(validator.ValidateArguments(descriptor, defaults.RootElement).IsValid);
        Assert.IsTrue(validator.ValidateArguments(descriptor, legacy.RootElement).IsValid);
        Assert.IsFalse(validator.ValidateArguments(descriptor, invalid.RootElement).IsValid);
    }

    [TestMethod]
    [DataRow("gbk", "D6D0CEC4")]
    [DataRow("gb18030", "D6D0CEC4")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Execute_LegacyEncoding_WritesExpectedBytesAndDecodesRawOutput(
        string encoding, string expectedHex)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This byte-level probe uses Windows PowerShell.");
            return;
        }

        var script = "$buffer=[IO.MemoryStream]::new();" +
            "[Console]::OpenStandardInput().CopyTo($buffer);" +
            "[Console]::Error.Write([BitConverter]::ToString($buffer.ToArray()).Replace('-',''));" +
            "$bytes=[byte[]](214,208,206,196);" +
            "[Console]::OpenStandardOutput().Write($bytes,0,$bytes.Length)";
        var result = await ExecuteAsync(CreateArguments(script, "powershell", encoding, "中文"));

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32(), result.OutputJson);
        Assert.AreEqual("中文", output.RootElement.GetProperty("stdout").GetString());
        Assert.AreEqual(expectedHex, output.RootElement.GetProperty("stderr").GetString());
    }

    private Task<ToolResult> ExecuteAsync(string argumentsJson)
    {
        var workspace = Path.GetTempPath();
        var permission = new ToolPermissionContext(
            "grant-encoding", "run-encoding", workspace, [workspace], [],
            AllowedExecutables: OperatingSystem.IsWindows()
                ? ["powershell", "pwsh", "cmd"] : ["sh", "bash", "zsh"],
            CommandWorkingDirectory: workspace);
        return new BuiltinCommandToolExecutor().ExecuteAsync(
            new ToolInvocation(Guid.NewGuid().ToString("N"), BuiltinToolRegistry.CommandToolId,
                "agent-encoding", null, argumentsJson, "run-encoding", "session-encoding",
                PermissionContext: permission),
            TestContext.CancellationToken);
    }

    private static string CreateArguments(string script, string shell, string? encoding, string? stdin = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("script", script);
            writer.WriteString("shell", shell);
            if (encoding is not null)
            {
                writer.WriteString("encoding", encoding);
            }

            if (stdin is not null)
            {
                writer.WriteString("stdin", stdin);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
