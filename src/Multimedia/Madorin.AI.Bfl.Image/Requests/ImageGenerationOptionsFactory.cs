using System.Text.Json.Nodes;
using Madorin.AI.Bfl.Image.Metadata;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.CommandLine.CommandLineParser;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
namespace Madorin.AI.Bfl.Image.Requests;
internal sealed class ImageGenerationOptionsFactory(WorkspacePaths paths)
{
 private static readonly HashSet<string> Allowed=new(["request-id","request-file","api-key","api-url","prompt","task","model","output-dir","timeout","try-run","verbose","no-wait","output-format","width","height","seed","safety-tolerance","image-format","provider-bfl-disable-pup"],StringComparer.Ordinal);
 private static readonly HashSet<string> Switches=new(["try-run","verbose","no-wait","provider-bfl-disable-pup"],StringComparer.Ordinal);
 public async Task<ImageGenerationOptions> CreateAsync(string[] args,SkillInvocation invocation)
 {
  var extracted=ImageAttachments.Extract(args);var direct=Parse(extracted.Remaining,Allowed,Switches);invocation.CredentialPresent=!string.IsNullOrWhiteSpace(direct.GetValueOrDefault("api-key"));
  if(direct.TryGetValue("timeout",out var dt))invocation.SetTimeout(ParsePositiveInt(dt,300,"timeout"));
  var fn=direct.GetValueOrDefault("request-file");ImageRequestFile? file=fn is null?null:await new BflImageRequestReader(paths,invocation).ReadAsync(fn,invocation.CancellationToken,direct.TryGetValue("try-run",out var tr)?tr=="true":null);
  var fileId=file is null?null:ImageParameterValues.Text(file.Content,"request_id");var id=direct.GetValueOrDefault("request-id")??fileId;
  if(!RequestIds.IsValidRequestId(id)||direct.TryGetValue("request-id",out var di)&&fileId is not null&&di!=fileId)throw new SkillException("invalid_parameter","request_id 缺失、无效或与请求文件冲突。");invocation.ResponseId=id;
  if(file is not null){ImageAttachments.Validate(file.Content["attachments"],file.Content.ContainsKey("attachments"),paths,file.BaseDirectory);if(file.Content["execution"] is JsonObject c&&c.ContainsKey("output_dir"))paths.Resolve(ImageParameterValues.Text(c,"output_dir")!,file.BaseDirectory);}
  ImageAttachments.Validate(extracted.Attachments,extracted.Attachments is not null,paths,null);
  var input=ImageInputEcho.Build(direct,file);input["request_id"]=S(id);var exec=input["execution"]!.AsObject();var pars=input["parameters"]!.AsObject();
  invocation.TryRun=ImageParameterValues.Boolean(exec,"try_run");var timeout=ImageParameterValues.Integer(exec,"timeout",300);invocation.SetTimeout(timeout);var verbose=ImageParameterValues.Boolean(exec,"verbose");
  if(ImageParameterValues.Boolean(exec,"no_wait"))throw new SkillException("invalid_parameter","BFL 技能当前不支持 no_wait=true。");if(ImageParameterValues.Text(exec,"output_format","json")!="json")throw new SkillException("invalid_parameter","标准输出格式仅支持 json。");
  var task=ImageParameterValues.Text(input,"task","text-to-image")!;var model=ImageParameterValues.Text(input,"model",BflImageMetadata.DefaultModel)!;invocation.Model=model;var prompt=ImageParameterValues.Text(input,"prompt");
  if(task!="text-to-image"||!ModelIds.IsValid(model)||string.IsNullOrWhiteSpace(prompt))throw new SkillException("invalid_parameter","任务、模型或提示词不合法。");
  var key=direct.GetValueOrDefault("api-key");var url=direct.GetValueOrDefault("api-url")??BflImageMetadata.DefaultApiUrl;if(string.IsNullOrWhiteSpace(key)||!Uri.TryCreate(url,UriKind.Absolute,out var endpoint)||endpoint.Scheme!="https"||endpoint.Query.Length>0||endpoint.Fragment.Length>0||endpoint.UserInfo.Length>0)throw new SkillException("invalid_parameter","必须提供 api-key 和有效 HTTPS API 地址。");
  var output=ImageParameterValues.Text(exec,"output_dir");if(!invocation.TryRun&&string.IsNullOrWhiteSpace(output))throw new SkillException("invalid_parameter","正式生成必须指定 output-dir。");var resolved=output is null?null:paths.Resolve(output,direct.ContainsKey("output-dir")?null:file?.BaseDirectory);
  if(input["attachments"] is JsonArray at&&at.Count>0)throw new SkillException("invalid_parameter","本批仅支持文生图，不支持图片附件。");
  int? width=OptionalInt(pars,"width",direct);int? height=OptionalInt(pars,"height",direct);if((width is null)!=(height is null)||width is <256 or >6256||height is <256 or >6256||(long)(width??256)*(height??256)>16_822_272)throw new SkillException("invalid_parameter","width/height 必须同时提供且满足 FLUX.2 像素限制。");
  long? seed=OptionalLong(pars,"seed",direct);if(seed<0)throw new SkillException("invalid_parameter","seed 不得小于零。");var safety=OptionalInt(pars,"safety_tolerance",direct)??2;if(safety is <0 or >5)throw new SkillException("invalid_parameter","FLUX.2 safety_tolerance 范围为 0 至 5。");
  var fmt=TextValue(pars,"output_format",direct,"jpeg"); if (direct.TryGetValue("image-format", out var directFormat)) fmt=directFormat;if(fmt is not ("jpeg" or "png" or "webp"))throw new SkillException("invalid_parameter","image-format 仅支持 jpeg、png 或 webp。");
  var providerParameters=input["provider_parameters"] as JsonObject;var bflParameters=providerParameters?["bfl"] as JsonObject;var disable=direct.TryGetValue("provider-bfl-disable-pup",out var dp)?dp=="true":BoolValue(bflParameters??pars,"disable_pup",BoolValue(pars,"disable_pup",false));
  foreach(var p in pars.ToArray())if(p.Key is not ("width" or "height" or "seed" or "safety_tolerance" or "output_format" or "disable_pup"))pars.Remove(p.Key);
  if(bflParameters is not null)foreach(var p in bflParameters.ToArray())if(p.Key!="disable_pup")bflParameters.Remove(p.Key);
  invocation.ThrowIfCancellationRequested();return new ImageGenerationOptions(id!,task,model,prompt!,key!,endpoint!,resolved,disable,seed,width,height,safety,fmt,timeout,invocation.TryRun,verbose,input);
 }
 private static int? OptionalInt(JsonObject p,string name,Dictionary<string,string?> d){var s=DirectOrNode(p,name,d);return s is null?null:int.TryParse(s,out var x)?x:throw new SkillException("invalid_parameter",name+" 必须为整数。");}
 private static long? OptionalLong(JsonObject p,string name,Dictionary<string,string?> d){var s=DirectOrNode(p,name,d);return s is null?null:long.TryParse(s,out var x)?x:throw new SkillException("invalid_parameter",name+" 必须为整数。");}
 private static string? DirectOrNode(JsonObject p,string n,Dictionary<string,string?> d){if(d.TryGetValue(n,out var v))return v;if(p[n] is JsonValue j&&j.TryGetValue<string>(out var s))return s;if(p[n] is JsonValue k&&k.TryGetValue<long>(out var l))return l.ToString(System.Globalization.CultureInfo.InvariantCulture);return null;}
 private static string TextValue(JsonObject p,string n,Dictionary<string,string?> d,string fallback)=>d.GetValueOrDefault(n)??ImageParameterValues.Text(p,n,fallback)!;
 private static bool BoolValue(JsonObject p,string n,bool f)=>p[n] is JsonValue v&&v.TryGetValue<bool>(out var b)?b:f;
}



