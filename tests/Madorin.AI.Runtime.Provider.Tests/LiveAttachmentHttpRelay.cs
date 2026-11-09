using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Madorin.AI.Runtime.Provider.Tests;

/// <summary>测试专用的本机 relay，核验附件后原样转发到唯一配置 origin，不落盘正文或鉴权头。</summary>
internal sealed class LiveAttachmentHttpRelay(WebApplication application, HttpClient http, string adapterBaseUrl)
    : IAsyncDisposable
{
    private static readonly string[] ForwardedHeaders =
        ["Authorization", "x-api-key", "anthropic-version", "User-Agent", "Accept"];

    public string AdapterBaseUrl { get; } = adapterBaseUrl;

    public static async Task<LiveAttachmentHttpRelay> StartAsync(Uri remote, Action<byte[], string> inspect,
        CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        app.Run(async context =>
        {
            // origin 固定，客户端不能指定转发主机；保留 SDK 生成的 path/query。
            var target = new Uri(remote.GetLeftPart(UriPartial.Authority) + context.Request.Path
                + context.Request.QueryString);
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
            await using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            var bytes = buffer.ToArray();
            inspect(bytes, target.AbsolutePath);
            request.Content = new ByteArrayContent(bytes);
            foreach (var name in ForwardedHeaders)
            {
                if (context.Request.Headers.TryGetValue(name, out var value))
                {
                    request.Headers.TryAddWithoutValidation(name, value.ToArray());
                }
            }
            if (context.Request.ContentType is { } contentType)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        try
        {
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            var adapterUrl = address.TrimEnd('/') + remote.AbsolutePath.TrimEnd('/');
            return new LiveAttachmentHttpRelay(app, http, adapterUrl);
        }
        catch
        {
            await app.DisposeAsync();
            http.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await application.StopAsync();
        await application.DisposeAsync();
        http.Dispose();
    }
}
