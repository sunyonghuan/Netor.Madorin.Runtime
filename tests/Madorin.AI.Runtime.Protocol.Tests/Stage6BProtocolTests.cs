using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class Stage6BProtocolTests
{
    [TestMethod]
    public void ExpertRequest_LegacyShape_StillDeserializes()
    {
        const string json =
            """
            {
              "sessionIdempotencyKey":"session-key",
              "runIdempotencyKey":"run-key",
              "mode":"Expert",
              "selection":{
                "selectionVersion":1,
                "mode":"Expert",
                "defaultSelection":{"providerId":"fake","modelId":"model"},
                "modeOptions":{
                  "mode":"expert",
                  "agent":{"agentId":"expert","promptTemplateVersion":"v1","systemPrompt":"Help"}
                }
              },
              "initialInput":[{"type":"text","text":"Hello"}]
            }
            """;

        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.NewSessionRunRequest);

        Assert.IsNotNull(actual);
        var options = Assert.IsInstanceOfType<ExpertModeOptions>(
            actual.Selection.ModeOptions);
        Assert.IsNull(options.Agent.AllowedToolIds);
        Assert.IsNull(options.Agent.SkillIds);
    }
}
