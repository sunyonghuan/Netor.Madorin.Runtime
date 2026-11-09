using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class BuiltinExecutionToolTests
{
    private string _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Join(Path.GetTempPath(), $"madorin-builtin-exec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Command_AllowlistedShell_ExecutesCommandAndReturnsOutput()
    {
        var permission = CreatePermission(
            allowedExecutables: GetShellNames(),
            allowedEnvironmentVariables: SkillScriptEnvironmentPolicy.InheritedVariableNames.ToArray());
        var result = await ExecuteAsync(
            new BuiltinCommandToolExecutor(),
            BuiltinToolRegistry.CommandToolId,
            $$"""{"script":"{{(OperatingSystem.IsWindows() ? "Write-Output command-ok" : "printf command-ok")}}"}""",
            permission);

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32());
        StringAssert.Contains(output.RootElement.GetProperty("stdout").GetString(), "command-ok");
    }

    [TestMethod]
    public async Task Command_AllowsMultipleCommandsAndUnicodeText()
    {
        var shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var script = OperatingSystem.IsWindows()
            ? "$value='第一行'; Write-Output $value; Write-Output '第二行'"
            : "printf '第一行\\n'; printf '第二行\\n'";
        var result = await ExecuteAsync(
            new BuiltinCommandToolExecutor(),
            BuiltinToolRegistry.CommandToolId,
            $$"""{"shell":"{{shell}}","script":"{{script}}"}""",
            CreatePermission(
                allowedExecutables: [shell],
                allowedEnvironmentVariables: SkillScriptEnvironmentPolicy.InheritedVariableNames.ToArray()));

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32());
        StringAssert.Contains(
            output.RootElement.GetProperty("stdout").GetString(),
            "第一行");
    }

    [TestMethod]
    public async Task Command_ExecutableAllowlist_DoesNotRestrictAuthorizedCommandTool()
    {
        var shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var result = await ExecuteAsync(
            new BuiltinCommandToolExecutor(),
            BuiltinToolRegistry.CommandToolId,
            $$"""{"shell":"{{shell}}","script":"echo allowed"}""",
            CreatePermission(
                allowedExecutables: ["dotnet"],
                allowedEnvironmentVariables: SkillScriptEnvironmentPolicy.InheritedVariableNames.ToArray()));

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        StringAssert.Contains(output.RootElement.GetProperty("stdout").GetString(), "allowed");
    }

    [TestMethod]
    public async Task Command_EnvironmentOverrides_DoNotRequireSeparateAuthorization()
    {
        var result = await ExecuteAsync(
            new BuiltinCommandToolExecutor(),
            BuiltinToolRegistry.CommandToolId,
            OperatingSystem.IsWindows()
                ? """{"shell":"powershell","script":"Write-Output $env:SECRET","environment":{"SECRET":"value"}}"""
                : """{"shell":"sh","script":"printf \"$SECRET\"","environment":{"SECRET":"value"}}""",
            CreatePermission(
                allowedExecutables: GetShellNames(),
                allowedEnvironmentVariables: SkillScriptEnvironmentPolicy.InheritedVariableNames.ToArray()));

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        StringAssert.Contains(output.RootElement.GetProperty("stdout").GetString(), "value");
    }

    [TestMethod]
    public async Task Command_DirectoryAuthorization_IsRequiredEvenWhenShellIsAllowlisted()
    {
        var result = await ExecuteAsync(new BuiltinCommandToolExecutor(), BuiltinToolRegistry.CommandToolId,
            """{"script":"echo blocked"}""", CreatePermission() with { CommandWorkingDirectory = null });
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "authorization for working directory");
    }

    [TestMethod]
    public async Task Command_LinkedWorkingDirectoryAndExecutable_AreHandledByOperatingSystem()
    {
        var directoryLink = Path.Combine(_root, "linked-directory");
        var realDirectory = Directory.CreateDirectory(Path.Combine(_root, "target-directory")).FullName;
        var executableLink = Path.Combine(realDirectory, OperatingSystem.IsWindows() ? "linked-command.exe" : "linked-command");
        var target = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
        try
        {
            Directory.CreateSymbolicLink(directoryLink, realDirectory);
            File.CreateSymbolicLink(executableLink, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive($"Symbolic link creation is unavailable: {ex.Message}");
            return;
        }

        var shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var script = OperatingSystem.IsWindows()
            ? "& './linked-command.exe' /d /c 'echo linked-ok'"
            : "'./linked-command' -c 'echo linked-ok'";
        var arguments = WriteCommandArguments(shell, script, directoryLink);
        var result = await ExecuteAsync(new BuiltinCommandToolExecutor(), BuiltinToolRegistry.CommandToolId,
            arguments, CreatePermission() with { CommandWorkingDirectory = directoryLink });
        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32(), result.OutputJson);
        StringAssert.Contains(output.RootElement.GetProperty("stdout").GetString(), "linked-ok");
    }

    [TestMethod]
    public async Task Command_WindowsPwshApplicationAlias_CanExecuteRepeatedly()
    {
        if (!OperatingSystem.IsWindows() || !(Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator).Any(path => File.Exists(Path.Join(path.Trim().Trim('"'), "pwsh.exe"))))
        {
            Assert.Inconclusive("Windows pwsh is not installed on PATH.");
            return;
        }

        foreach (var shell in new[] { "pwsh", "cmd", "pwsh", "auto" })
        {
            var result = await ExecuteAsync(new BuiltinCommandToolExecutor(), BuiltinToolRegistry.CommandToolId,
                WriteCommandArguments(shell, "echo shell-ok", _root), CreatePermission(allowedExecutables: []));
            Assert.IsTrue(result.Success, result.Error);
            using var output = JsonDocument.Parse(result.OutputJson!);
            Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32(), result.OutputJson);
            StringAssert.Contains(output.RootElement.GetProperty("stdout").GetString(), "shell-ok");
        }
    }

    private static string WriteCommandArguments(string shell, string script, string directory)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("shell", shell);
            writer.WriteString("script", script);
            writer.WriteString("workingDirectory", directory);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [TestMethod]
    public async Task Command_SafeInheritedEnvironment_AllowsShellExecution()
    {
        var projectPath = Path.Combine(_root, "EnvironmentProbe.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """,
            TestContext.CancellationToken);
        var permission = CreatePermission(
            allowedExecutables: GetShellNames(),
            allowedEnvironmentVariables: SkillScriptEnvironmentPolicy.InheritedVariableNames.ToArray());

        var result = await ExecuteAsync(
            new BuiltinCommandToolExecutor(),
            BuiltinToolRegistry.CommandToolId,
            $$"""{"script":"{{(OperatingSystem.IsWindows() ? "Write-Output environment-ok" : "printf environment-ok")}}"}""",
            permission);

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32());
        StringAssert.Contains(output.RootElement.GetProperty("stdout").GetString(), "environment-ok");
    }

    [TestMethod]
    public async Task HttpRequest_DefaultDeny_IsRejectedBeforeConnect()
    {
        var result = await ExecuteAsync(
            new BuiltinHttpToolExecutor(),
            BuiltinToolRegistry.HttpRequestToolId,
            """{"method":"GET","url":"http://127.0.0.1/"}""",
            CreatePermission());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "denied");
    }

    [TestMethod]
    public async Task HttpRequest_ExplicitLoopbackPolicy_UsesPinnedAddress()
    {
        var server = StartServer(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello");
        await using var serverScope = server;
        var policy = CreateLoopbackPolicy(server.Port, maxResponseBytes: 1024);
        var arguments = $$"""{"method":"GET","url":"http://127.0.0.1:{{server.Port}}/value"}""";

        var result = await ExecuteAsync(
            new BuiltinHttpToolExecutor(),
            BuiltinToolRegistry.HttpRequestToolId,
            arguments,
            CreatePermission(networkPolicy: policy));
        await server.Completion.WaitAsync(TestContext.CancellationToken);

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(200, output.RootElement.GetProperty("statusCode").GetInt32());
        Assert.AreEqual("hello", output.RootElement.GetProperty("body").GetString());
    }

    [TestMethod]
    public async Task HttpRequest_ResponseExceedsPolicy_IsRejected()
    {
        var server = StartServer(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 8\r\nConnection: close\r\n\r\ntoo-long");
        await using var serverScope = server;
        var policy = CreateLoopbackPolicy(server.Port, maxResponseBytes: 4);
        var arguments = $$"""{"method":"GET","url":"http://127.0.0.1:{{server.Port}}/large"}""";

        var result = await ExecuteAsync(
            new BuiltinHttpToolExecutor(),
            BuiltinToolRegistry.HttpRequestToolId,
            arguments,
            CreatePermission(networkPolicy: policy));
        await server.Completion.WaitAsync(TestContext.CancellationToken);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "limit");
    }

    [TestMethod]
    public async Task HttpRequest_LoopbackWithoutExplicitAddressRange_IsRejected()
    {
        var policy = new NetworkPolicy(
            DenyAll: false,
            AllowedHosts: ["127.0.0.1"],
            AllowedSchemes: ["http"],
            AllowedPorts: [8080]);
        var result = await ExecuteAsync(
            new BuiltinHttpToolExecutor(),
            BuiltinToolRegistry.HttpRequestToolId,
            """{"method":"GET","url":"http://127.0.0.1:8080/"}""",
            CreatePermission(networkPolicy: policy));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "unauthorized address");
    }

    [TestMethod]
    public async Task HttpRequest_RedirectToUnauthorizedAddress_IsRejectedBeforeSecondConnect()
    {
        var server = StartServer(
            "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await using var serverScope = server;
        var policy = new NetworkPolicy(
            DenyAll: false,
            AllowedHosts: ["127.0.0.1", "169.254.169.254"],
            AllowedSchemes: ["http"],
            AllowedPorts: [server.Port, 80],
            AllowedAddressRanges: ["127.0.0.0/8"],
            MaxRedirects: 1,
            MaxResponseBytes: 1024);
        var arguments = $$"""{"method":"GET","url":"http://127.0.0.1:{{server.Port}}/redirect"}""";

        var result = await ExecuteAsync(
            new BuiltinHttpToolExecutor(),
            BuiltinToolRegistry.HttpRequestToolId,
            arguments,
            CreatePermission(networkPolicy: policy));
        await server.Completion.WaitAsync(TestContext.CancellationToken);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "unauthorized address");
    }

    private Task<ToolResult> ExecuteAsync(
        IToolExecutor executor,
        string toolId,
        string argumentsJson,
        ToolPermissionContext permission) => executor.ExecuteAsync(
        new ToolInvocation(
            Guid.NewGuid().ToString("N"),
            toolId,
            "agent-1",
            ParentAgentId: null,
            argumentsJson,
            "run-1",
            "session-1",
            PermissionContext: permission),
        TestContext.CancellationToken);

    private ToolPermissionContext CreatePermission(
        string[]? allowedExecutables = null,
        string[]? allowedEnvironmentVariables = null,
        NetworkPolicy? networkPolicy = null) => new(
        "grant-1",
        "run-1",
        _root,
        [_root],
        [_root],
        AllowedExecutables: allowedExecutables ?? GetShellNames(),
        DenyNetwork: networkPolicy?.DenyAll ?? true,
        AllowedEnvironmentVariables: allowedEnvironmentVariables
            ?? SkillScriptEnvironmentPolicy.InheritedVariableNames.ToArray(),
        NetworkPolicy: networkPolicy,
        CommandWorkingDirectory: _root);

    private static string[] GetShellNames() =>
        OperatingSystem.IsWindows() ? ["pwsh", "powershell", "cmd"] : ["bash", "zsh", "sh"];

    private static NetworkPolicy CreateLoopbackPolicy(int port, long maxResponseBytes) => new(
        DenyAll: false,
        AllowedHosts: ["127.0.0.1"],
        AllowedSchemes: ["http"],
        AllowedPorts: [port],
        AllowedAddressRanges: ["127.0.0.0/8"],
        MaxRedirects: 0,
        MaxResponseBytes: maxResponseBytes);

    private static TestHttpServer StartServer(string response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var completion = ServeOnceAsync(listener, response);
        return new TestHttpServer(listener, port, completion);
    }

    private static async Task ServeOnceAsync(TcpListener listener, string response)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var buffer = new byte[4096];
        var received = new StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (received.Length > 64 * 1024)
            {
                throw new InvalidDataException("Test HTTP request headers exceeded the limit.");
            }
        }

        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private sealed class TestHttpServer(
        TcpListener listener,
        int port,
        Task completion) : IAsyncDisposable
    {
        public Task Completion { get; } = completion;

        public int Port { get; } = port;

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
