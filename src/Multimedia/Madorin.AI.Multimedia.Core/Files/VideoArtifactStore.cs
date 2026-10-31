using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Multimedia.Core.Files;

/// <summary>有界流式下载、完整验证和无覆盖的视频落盘事务。</summary>
public sealed class VideoArtifactStore(WorkspacePaths paths) : IDisposable
{
    private readonly List<string> _written = [];
    public async Task<JsonObject> WriteAsync(Stream source, string outputDirectory, CancellationToken cancellationToken)
    {
        var target = paths.Resolve(outputDirectory);
        var temporary = Path.Combine(target, ".video-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.EnsureAllowed(target);
            Directory.CreateDirectory(target);
            paths.EnsureAllowed(temporary);
            long length = 0;
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    length += count;
                    if (length > 536870912) { throw new SkillException("provider_error", "视频超过 512 MiB 本地下载上限。"); }
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }
            var info = await VideoValidator.ValidateAsync(temporary, cancellationToken);
            for (var sequence = 1; ; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = $"video_{sequence:000}.mp4";
                var output = paths.Resolve(name, target);
                paths.EnsureAllowed(temporary);
                try { File.Move(temporary, output); }
                catch (IOException) when (File.Exists(output)) { continue; }
                _written.Add(output);
                info["index"] = N(sequence);
                info["path"] = S(name);
                info["media_type"] = S("video/mp4");
                info["format"] = S("mp4");
                info["size_bytes"] = N(length);
                return info;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SkillException("file_write_error", "无法写入视频产物。");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                TryDelete(temporary);
            }
        }
    }
    public void Commit() => _written.Clear();
    public void Dispose()
    {
        foreach (var file in _written) { TryDelete(file); }
        _written.Clear();
    }
    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
