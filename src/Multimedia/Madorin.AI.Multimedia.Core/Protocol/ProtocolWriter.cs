using System.Text.Json.Nodes;

namespace Madorin.AI.Multimedia.Core.Protocol;

public sealed class ProtocolWriter(TextWriter output)
{
    public int Write(JsonNode response, int exitCode = 0)
    {
        output.WriteLine(response.ToJsonString(JsonProtocol.Options));
        return exitCode;
    }

    public static int ExitCode(string code) => code switch
    {
        "invalid_parameter" => 1,
        "authentication_failed" => 2,
        "insufficient_balance" or "content_policy_violation" or "rate_limit_exceeded" or "task_not_ready" => 3,
        "timeout" => 4,
        "file_write_error" => 5,
        "network_error" => 6,
        "cancelled" => 130,
        _ => 125
    };
}
