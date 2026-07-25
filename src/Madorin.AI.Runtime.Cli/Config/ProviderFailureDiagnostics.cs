using System.Net;
using System.Text.Json;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Cli.Config;

internal static class ProviderFailureDiagnostics
{
    public static string Describe(Exception exception) => exception switch
    {
        ProviderHttpException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "authentication failed.",
        ProviderHttpException { StatusCode: HttpStatusCode.RequestTimeout } =>
            "request timed out.",
        ProviderHttpException { StatusCode: HttpStatusCode.TooManyRequests } =>
            "Provider rate limit was reached.",
        ProviderHttpException { StatusCode: >= HttpStatusCode.InternalServerError } httpFailure =>
            $"Provider service returned HTTP {(int)httpFailure.StatusCode!.Value}.",
        ProviderHttpException { StatusCode: not null } httpFailure =>
            $"Provider service returned HTTP {(int)httpFailure.StatusCode.Value}.",
        InvalidDataException or JsonException =>
            "Provider returned an invalid protocol response.",
        OperationCanceledException =>
            "request timed out.",
        HttpRequestException =>
            "connection failed.",
        ArgumentException or InvalidOperationException =>
            "Provider configuration is invalid.",
        _ => "unexpected Provider failure."
    };
}
