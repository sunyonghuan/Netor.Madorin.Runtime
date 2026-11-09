using System.Text.Json.Nodes;
using Madorin.AI.Bfl.Image.Requests;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
namespace Madorin.AI.Bfl.Image.Cli;
internal static class ImageTryRunResponse
{
 private static JsonObject Parameters(ImageGenerationOptions o)=>Obj(("width",o.Width is null?null:N(o.Width.Value)),("height",o.Height is null?null:N(o.Height.Value)),("seed",o.Seed is null?null:N(o.Seed.Value)),("safety_tolerance",N(o.SafetyTolerance)),("output_format",S(o.ImageFormat)),("disable_pup",B(o.DisablePromptUpsampling)));
 public static JsonObject Build(ImageGenerationOptions o,SkillInvocation i,JsonObject preview){var execution=Obj(("timeout",N(o.TimeoutSeconds)),("try_run",B(o.TryRun)),("verbose",B(o.Verbose)),("no_wait",B(false)),("output_format",S("json")));if(o.OutputDirectory is not null)execution["output_dir"]=o.InputParameters["execution"]?["output_dir"]?.DeepClone();var eff=Obj(("schema_version",S(SkillProtocol.ContractVersion)),("request_id",S(o.RequestId)),("prompt",S(o.Prompt)),("task",S(o.Task)),("model",S(o.Model)),("parameters",Parameters(o)),("attachments",new JsonArray()),("provider_parameters",new JsonObject()),("execution",execution));return Obj(("schema_version",S(SkillProtocol.ContractVersion)),("response_id",S(o.RequestId)),("status",S("validated")),("mode",S("try_run")),("provider",S("bfl")),("model",S(o.Model)),("input_parameters",o.InputParameters),("effective_parameters",eff),("credential_presence",Obj(("api_key",B(i.CredentialPresent)))),("request_preview",preview),("unicode_checks",SkillResponses.UnicodeChecks(o.InputParameters)),("errors",new JsonArray()),("ignored_parameters",i.IgnoredParameters.DeepClone()),("converted_parameters",i.ConvertedParameters.DeepClone()),("side_effects",SkillResponses.NoSideEffects()));}
}




