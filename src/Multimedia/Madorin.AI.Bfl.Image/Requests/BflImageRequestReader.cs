using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;

namespace Madorin.AI.Bfl.Image.Requests;

internal sealed class BflImageRequestReader(WorkspacePaths paths, SkillInvocation invocation)
{
    public async Task<ImageRequestFile> ReadAsync(string file, CancellationToken cancellationToken, bool? tryRunOverride = null)
    {
        var result = await new GenerationRequestReader(paths, invocation).ReadAsync(file, cancellationToken, tryRunOverride);
        return new(result.Content, result.BaseDirectory);
    }
}
