namespace Madorin.AI.Grok.Video.Provider;

internal sealed record VideoJob(string TaskId, string Status, string? Url, int? DurationSeconds, string? Model, string? ErrorCode);
