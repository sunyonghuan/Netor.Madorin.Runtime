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
using Madorin.AI.Runtime.Contracts.Serialization;
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
        // Synchronous non-streaming run endpoint.
        // Always returns a single buffered JSON response (HttpRunResponse).
        // Internally we still drive the full streaming Run event loop and buffer events,
        // because a Runtime Run is a multi-turn agent process (LLM + tools + state),
        // not a single provider generation. Setting Stream=false in RequestControl
        // is propagated as a hint for inner provider calls.
        app.MapPost("/v1/run", async (HttpRunRequest request, HttpContext httpContext, CancellationToken ct) =>
            await HandleRunAsync(request, httpContext, ct))
            .DisableAntiforgery();

        app.MapGet("/v1/health", () => Results.Ok(new HealthResponse { Status = "healthy" }));
    }

    private async Task<IResult> HandleRunAsync(HttpRunRequest request, HttpContext httpContext, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Determine sync timeout (default 300s as per plan).
        // Priority:
        //   1. RequestControl.TimeoutMs (in milliseconds)
        //   2. Header "X-Timeout-Seconds"
        //   3. 300 seconds
        var timeoutSeconds = 300;

        if (request.RequestControl?.TimeoutMs is > 0)
        {
            timeoutSeconds = Math.Max(1, request.RequestControl.TimeoutMs.Value / 1000);
        }
        else if (httpContext.Request.Headers.TryGetValue("X-Timeout-Seconds", out var headerValues)
                 && int.TryParse(headerValues.ToString(), out var headerTimeout)
                 && headerTimeout > 0)
        {
            timeoutSeconds = headerTimeout;
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var effectiveCt = linkedCts.Token;

        try
        {
            // Validate RequestControl early (fail fast with 400 before expensive work).
            // This covers Temperature, MaxTokens, TopP, StopSequences, TimeoutMs, etc.
            // Must be inside the try so the dedicated ArgumentException handler below catches it.
            var forcedControl = (request.RequestControl ?? new ExpertRequestControl()) with { Stream = false };
            forcedControl.Validate();

            // Force non-streaming intent for the synchronous HTTP path.
            // This is propagated via RequestControl into the Run selection and
            // down to provider requests as a hint (see RuntimeProviderRequest.Stream).
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

            // The selection already carries forcedControl (Stream=false) in ModeOptions.RequestControl.
            // This will flow into every RuntimeProviderRequest created inside the Expert orchestrator.
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

            // Buffer the entire event stream. This is required because a Runtime Run
            // is a multi-turn agent loop (LLM calls + tools + plan state + re-injection),
            // not a single provider call. We cannot simply forward a provider-level
            // non-stream response; we must wait for the Run to reach a terminal state.
            //
            // We force Stream=false at the RequestControl level as a strong hint
            // for inner provider generations.
            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in client.ReadEventsAsync(0, effectiveCt))
            {
                if (!string.Equals(envelope.RunId, actualRunId, StringComparison.Ordinal))
                {
                    continue;
                }

                events.Add(envelope);
                if (IsTerminalEvent(envelope.MessageType))
                {
                    break;
                }
            }

            sw.Stop();
            var run = await client.QueryRunAsync(actualRunId, effectiveCt);
            var response = new HttpRunResponse
            {
                RunId = actualRunId,
                SessionId = run.SessionId,
                Output = ExtractFinalContent(events),
                Status = ExtractFinalStatus(events) ?? "Unknown",
                ErrorMessage = ExtractErrorMessage(events),
                ElapsedMs = sw.ElapsedMilliseconds,
                InputTokens = ExtractInputTokens(events),
                OutputTokens = ExtractOutputTokens(events)
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
        catch (ArgumentException ex)
        {
            // Validation error (RequestControl.Validate, selection, etc.)
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

            // For clearly client-caused issues (bad provider, invalid args that reached here), prefer 4xx.
            // Otherwise fall back to 500.
            var isClientError = ex is ArgumentException
                                || ex.Message.Contains("provider", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

            return Results.Problem(
                detail: ex.Message,
                statusCode: isClientError ? 400 : 500,
                title: isClientError ? "Bad Request" : "Run execution failed");
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

    private static bool IsTerminalEvent(string messageType) =>
        string.Equals(messageType, "run.completed", StringComparison.Ordinal)
        || string.Equals(messageType, "run.failed", StringComparison.Ordinal)
        || string.Equals(messageType, "run.cancelled", StringComparison.Ordinal);

    private static string ExtractFinalContent(List<RuntimeEventEnvelope> events)
    {
        var sb = new StringBuilder();
        foreach (var evt in events)
        {
            if (string.Equals(evt.MessageType, "output.delta", StringComparison.Ordinal))
            {
                try
                {
                    var delta = JsonSerializer.Deserialize(evt.Payload, RuntimeJsonContext.Default.TextDeltaEvent);
                    if (delta?.Delta != null)
                    {
                        sb.Append(delta.Delta);
                    }
                }
                catch
                {
                    // ignore malformed delta
                }
            }
        }

        return sb.ToString();
    }

    private static string? ExtractFinalStatus(List<RuntimeEventEnvelope> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var t = events[i].MessageType;
            if (string.Equals(t, "run.completed", StringComparison.Ordinal))
            {
                return "Completed";
            }

            if (string.Equals(t, "run.failed", StringComparison.Ordinal))
            {
                return "Failed";
            }

            if (string.Equals(t, "run.cancelled", StringComparison.Ordinal))
            {
                return "Cancelled";
            }
        }

        return null;
    }

    private static string? ExtractErrorMessage(List<RuntimeEventEnvelope> events)
    {
        foreach (var evt in events)
        {
            if (string.Equals(evt.MessageType, "run.failed", StringComparison.Ordinal))
            {
                try
                {
                    var failed = JsonSerializer.Deserialize(evt.Payload, RuntimeJsonContext.Default.RunFailedEvent);
                    return failed?.Error.Message;
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static int? ExtractInputTokens(List<RuntimeEventEnvelope> events)
    {
        foreach (var evt in events)
        {
            if (string.Equals(evt.MessageType, "usage.updated", StringComparison.Ordinal))
            {
                try
                {
                    var usage = JsonSerializer.Deserialize(evt.Payload, RuntimeJsonContext.Default.UsageUpdatedEvent);
                    return usage?.InputTokens;
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static int? ExtractOutputTokens(List<RuntimeEventEnvelope> events)
    {
        foreach (var evt in events)
        {
            if (string.Equals(evt.MessageType, "usage.updated", StringComparison.Ordinal))
            {
                try
                {
                    var usage = JsonSerializer.Deserialize(evt.Payload, RuntimeJsonContext.Default.UsageUpdatedEvent);
                    return usage?.OutputTokens;
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
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


