using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Bfl.Image.Requests;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
namespace Madorin.AI.Bfl.Image.Provider;
internal sealed class BflImageClient(HttpClient client, TimeSpan? pollInterval = null)
{
 private readonly TimeSpan _pollInterval=pollInterval??TimeSpan.FromMilliseconds(500);
 public static BflImageRequest Map(ImageGenerationOptions o)=>new(o.Prompt,o.DisablePromptUpsampling,o.Seed,o.Width,o.Height,o.SafetyTolerance,o.ImageFormat);
 public static JsonObject Preview(BflImageRequest r)=>JsonSerializer.SerializeToNode(r,BflJsonContext.Default.BflImageRequest)!.AsObject();
 public async Task<JsonArray> GenerateAsync(ImageGenerationOptions o,BflImageRequest request,SkillInvocation i,ArtifactStore a)
 {
  using var m=new HttpRequestMessage(HttpMethod.Post,Endpoint(o.Endpoint,"v1/flux-2-pro")); m.Headers.TryAddWithoutValidation("x-key",o.ApiKey); m.Content=JsonContent.Create(request,BflJsonContext.Default.BflImageRequest);
  using var r=await client.SendAsync(m,HttpCompletionOption.ResponseHeadersRead,i.CancellationToken); Trace(r,i,o.ApiKey); var body=await r.Content.ReadAsStringAsync(i.CancellationToken); if(!r.IsSuccessStatusCode)throw Error(r.StatusCode);
  using var d=Parse(body); var id=GetString(d.RootElement,"id"); var poll=GetString(d.RootElement,"polling_url"); if(!Safe(id,o.ApiKey)||!PollUrl(poll,o.Endpoint))throw new SkillException("provider_error","BFL 提交响应缺少有效任务信息。"); i.TaskId=id;
  while(true){i.ThrowIfCancellationRequested(); await Task.Delay(_pollInterval,i.CancellationToken); using var q=new HttpRequestMessage(HttpMethod.Get,poll);q.Headers.TryAddWithoutValidation("x-key",o.ApiKey);using var qr=await client.SendAsync(q,HttpCompletionOption.ResponseHeadersRead,i.CancellationToken);Trace(qr,i,o.ApiKey);var b=await qr.Content.ReadAsStringAsync(i.CancellationToken);if(!qr.IsSuccessStatusCode)throw Error(qr.StatusCode);using var x=Parse(b);var root=x.RootElement;if(GetString(root,"id")!=id)throw new SkillException("provider_error","BFL 查询响应任务 ID 不匹配。");var status=GetString(root,"status");i.RemoteStatus=status;if(status is "Pending" or "Reasoning" or "Generating")continue;if(status!="Ready")throw new SkillException(status is "Request Moderated" or "Content Moderated"?"content_policy_violation":"provider_error","BFL 图片任务未成功完成。",status);if(!root.TryGetProperty("result",out var result)||result.ValueKind!=JsonValueKind.Object||!Delivery(GetString(result,"sample")))throw new SkillException("provider_error","BFL 完成响应缺少有效图片地址。");return await Download(GetString(result,"sample")!,o.OutputDirectory!,o.ImageFormat,i,a);}
 }
 private async Task<JsonArray> Download(string url,string output,string expectedFormat,SkillInvocation i,ArtifactStore a){if(!Delivery(url))throw new SkillException("provider_error","BFL 图片地址不受信任。");using var r=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,i.CancellationToken);if(!r.IsSuccessStatusCode)throw new SkillException("provider_error","BFL 图片下载失败。");var mime=r.Content.Headers.ContentType?.MediaType;await using var s=await r.Content.ReadAsStreamAsync(i.CancellationToken);using var d=new MemoryStream();var buf=new byte[81920];while(true){var n=await s.ReadAsync(buf,i.CancellationToken);if(n==0)break;if(d.Length+n>50*1024*1024)throw new SkillException("provider_error","BFL 图片超过本地处理上限。");d.Write(buf,0,n);}var f=Format(d.ToArray(),mime);if(f!=expectedFormat)throw new SkillException("provider_error","BFL 返回图片格式与请求不一致。");var item=await a.WriteImageAsync(d.ToArray(),f,output,1,i.CancellationToken);return new JsonArray((JsonNode)item);}
 private static Uri Endpoint(Uri r,string s)=>new(new Uri(r.GetLeftPart(UriPartial.Path).TrimEnd('/')+"/"),s);
 private static JsonDocument Parse(string b){try{return JsonDocument.Parse(b);}catch(JsonException){throw new SkillException("provider_error","BFL 响应不是有效 JSON。");}}
 private static bool PollUrl(string?s,Uri root)=>Uri.TryCreate(s,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.UserInfo.Length==0&&u.Fragment.Length==0&&(u.IdnHost.Equals(root.IdnHost,StringComparison.OrdinalIgnoreCase)||u.IdnHost.EndsWith(".bfl.ai",StringComparison.OrdinalIgnoreCase))&&u.AbsolutePath.StartsWith("/v1/",StringComparison.Ordinal);
 private static bool Delivery(string?s)=>Uri.TryCreate(s,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.UserInfo.Length==0&&u.Fragment.Length==0&&u.IdnHost.StartsWith("delivery.",StringComparison.OrdinalIgnoreCase)&&u.IdnHost.EndsWith(".bfl.ai",StringComparison.OrdinalIgnoreCase);
 private static bool Safe(string?s,string key)=>!string.IsNullOrWhiteSpace(s)&&s.Length<=128&&!s.Any(char.IsControl)&&!s.Contains(key,StringComparison.Ordinal);
 private static void Trace(HttpResponseMessage r,SkillInvocation i,string key){if(r.Headers.TryGetValues("x-request-id",out var ids)){var v=ids.FirstOrDefault();if(Safe(v,key))i.ProviderRequestId=v;}}
  private static string Format(byte[] b,string?mime){var actual=b.Length>=8&&b.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})?"png":b.Length>=3&&b[0]==255&&b[1]==216&&b[2]==255?"jpeg":b.Length>=12&&b.AsSpan(0,4).SequenceEqual("RIFF"u8)&&b.AsSpan(8,4).SequenceEqual("WEBP"u8)?"webp":null;var declared=mime switch{"image/png"=>"png","image/jpeg"=>"jpeg","image/webp"=>"webp",null=>null,_=>throw new SkillException("provider_error","BFL 返回了不支持的图片 MIME 类型。")} ;if(actual is null||declared is not null&&declared!=actual)throw new SkillException("provider_error","BFL 图片内容与 MIME 类型不匹配或容器无效。");return actual!;}
 private static SkillException Error(HttpStatusCode s)=>new(s switch{HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>"authentication_failed",HttpStatusCode.PaymentRequired=>"insufficient_balance",HttpStatusCode.TooManyRequests=>"rate_limit_exceeded",HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout=>"timeout",_=>"provider_error"},"BFL 请求未能完成。",((int)s).ToString(System.Globalization.CultureInfo.InvariantCulture));
}
