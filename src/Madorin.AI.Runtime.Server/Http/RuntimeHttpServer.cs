using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Http;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Server.Http;

/// <summary>
/// HTTP REST API server for Runtime, providing synchronous non-streaming endpoints.
/// </summary>
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access",
    Justification = "ASP.NET Core minimal API reflection is acceptable for HTTP server component")]
[UnconditionalSuppressMessage(
    "AOT",
    "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality",
    Justification = "ASP.NET Core minimal API reflection is acceptable for HTTP server component")]
internal sealed partial class RuntimeHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ILogger<RuntimeHttpServer> _logger;
    private readonly string _pipeName;
    private readonly string _instanceId;
    private readonly string _handshakeSecret;

    public RuntimeHttpServer(
        string host,
        int port,
        string pipeName,
        string instanceId,
        string handshakeSecret,
        ILoggerFactory loggerFactory)
    {
        _pipeName = pipeName;
        _instanceId = instanceId;
        _handshakeSecret = handshakeSecret;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "Madorin.AI.Runtime.HttpServer"
        });

        builder.Services.AddSingleton(loggerFactory);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new LoggerFactoryProvider(loggerFactory));

        builder.Services.AddAntiforgery(options => options.SuppressXFrameOptionsHeader = true);

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, RuntimeHttpJsonContext.Default);
        });

        _app = builder.Build();
        _app.UseAntiforgery();
        _app.Urls.Clear();
        _app.Urls.Add($"http://{host}:{port}");
        _logger = loggerFactory.CreateLogger<RuntimeHttpServer>();

        MapEndpoints(_app);
    }

    private void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/run", HandleRunAsync).DisableAntiforgery();
        app.MapGet("/v1/health", () => Results.Ok(new HealthResponse { Status = "healthy" }));
    }

    private async Task<IResult> HandleRunAsync(HttpRunRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Determine sync timeout (default 300s as per plan).
        // Priority: RequestControl.TimeoutMs (ms) > 300s default.
        // (Header override can be added later via HttpContext if required.)
        var timeoutSeconds = 300;
        if (request.RequestControl?.TimeoutMs is > 0)
        {
            timeoutSeconds = Math.Max(1, request.RequestControl.TimeoutMs.Value / 1000);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var effectiveCt = linkedCts.Token;

        try
        {
            // Validate RequestControl parameters early to return clear HTTP 400 errors.
            // For the synchronous HTTP path we force Stream=false intent so the caller
            // receives a single buffered response instead of an event stream.
            var forcedControl = (request.RequestControl ?? new ExpertRequestControl()) with { Stream = false };
            forcedControl.Validate();

            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    _pipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: _instanceId,
                    HandshakeSecret: _handshakeSecret));

            await client.ConnectAsync(effectiveCt);

            var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
            var runId = Guid.NewGuid().ToString("N");

            var selection = new NextTurnSelection(
                SelectionVersion: 1,
                Mode: RuntimeMode.Expert,
                DefaultSelection: new DefaultSelection(
                    request.ProviderId ?? "default",
                    request.ModelId),
                ModeOptions: new ExpertModeOptions(
                    agent: new AgentRef(
                        "http-agent",
                        "1",
                        request.SystemPrompt ?? "You are a helpful AI assistant.",
                        ProviderId: request.ProviderId,
                        ModelId: request.ModelId),
                    availableAgents: null,
                    contextPolicy: null,
                    requestControl: forcedControl));

            var runRequest = string.IsNullOrWhiteSpace(request.SessionId)
                ? new NewSessionRunRequest(
                    sessionId,
                    runId,
                    RuntimeMode.Expert,
                    selection,
                    [new TextContentBlock(request.Input)],
                    Language: request.Language)
                : (object)new ExistingSessionRunRequest(
                    request.SessionId,
                    runId,
                    selection,
                    [new TextContentBlock(request.Input)]);

            var actualRunId = runRequest switch
            {
                NewSessionRunRequest newReq => await client.StartNewSessionRunAsync(newReq, effectiveCt),
                ExistingSessionRunRequest existingReq => await client.StartExistingSessionRunAsync(
                    existingReq,
                    effectiveCt),
                _ => throw new InvalidOperationException("Invalid run request type")
            };

            var output = new StringBuilder();
            string? status = null;
            string? errorMessage = null;
            int? inputTokens = null;
            int? outputTokens = null;

            // Buffer the entire event stream. This is required because a Runtime Run
            // is a multi-turn agent loop (LLM calls + tools + plan state + re-injection),
            // not a single provider call. We cannot simply forward a provider-level
            // non-stream response; we must wait for the Run to reach a terminal state.
            await foreach (var evt in client.ReadEventsAsync(0, effectiveCt))
            {
                switch (evt.MessageType)
                {
                    case "output.delta":
                        var delta = JsonSerializer.Deserialize<TextDeltaEvent>(
                            evt.Payload.GetRawText());
                        if (delta?.Delta != null)
                        {
                            output.Append(delta.Delta);
                        }

                        break;

                    case "run.completed":
                        status = "Completed";
                        break;

                    case "run.failed":
                        status = "Failed";
                        var failedEvt = JsonSerializer.Deserialize<RunFailedEvent>(
                            evt.Payload.GetRawText());
                        errorMessage = failedEvt?.Error.Message;
                        break;

                    case "run.cancelled":
                        status = "Cancelled";
                        break;

                    case "usage.updated":
                        var usageEvt = JsonSerializer.Deserialize<UsageUpdatedEvent>(
                            evt.Payload.GetRawText());
                        if (usageEvt != null)
                        {
                            inputTokens = usageEvt.InputTokens;
                            outputTokens = usageEvt.OutputTokens;
                        }

                        break;
                }

                if (status != null)
                {
                    break;
                }
            }

            sw.Stop();

            var response = new HttpRunResponse
            {
                RunId = actualRunId,
                SessionId = sessionId,
                Output = output.ToString(),
                Status = status ?? "Unknown",
                ErrorMessage = errorMessage,
                ElapsedMs = sw.ElapsedMilliseconds,
                InputTokens = inputTokens,
                OutputTokens = outputTokens
            };

            return Results.Ok(response);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            sw.Stop();
            Log.HttpRunTimeout(_logger, timeoutSeconds);
            return Results.Problem(
                detail: $"Request exceeded the synchronous timeout of {timeoutSeconds} seconds.",
                statusCode: 504,
                title: "Gateway Timeout");
        }
        catch (ArgumentException ex) when (ex.ParamName != null)
        {
            // Validation error from RequestControl.Validate()
            Log.HttpRunValidationFailed(_logger, ex);
            sw.Stop();
            return Results.Problem(
                detail: ex.Message,
                statusCode: 400,
                title: "Invalid request parameters");
        }
        catch (Exception ex)
        {
            Log.HttpRunFailed(_logger, ex);
            sw.Stop();
            return Results.Problem(
                detail: ex.Message,
                statusCode: 500,
                title: "Run execution failed");
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _app.StartAsync(ct);
        Log.HttpServerStarted(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class LoggerFactoryProvider : ILoggerProvider
    {
        private readonly ILoggerFactory _factory;

        public LoggerFactoryProvider(ILoggerFactory factory) => _factory = factory;

        public ILogger CreateLogger(string categoryName) => _factory.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Error,
            Message = "HTTP run request failed")]
        public static partial void HttpRunFailed(ILogger logger, Exception ex);

        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Information,
            Message = "HTTP server started")]
        public static partial void HttpServerStarted(ILogger logger);

        [LoggerMessage(
            EventId = 3,
            Level = LogLevel.Warning,
            Message = "HTTP run request validation failed")]
        public static partial void HttpRunValidationFailed(ILogger logger, Exception ex);

        [LoggerMessage(
            EventId = 4,
            Level = LogLevel.Warning,
            Message = "HTTP run request timed out after {TimeoutSeconds} seconds")]
        public static partial void HttpRunTimeout(ILogger logger, int timeoutSeconds);
    }
}


