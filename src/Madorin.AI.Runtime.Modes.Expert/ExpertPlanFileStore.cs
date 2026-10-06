using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts.Expert;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Modes.Expert;

/// <summary>Expert 计划文件持久化（保存到 .madorin/plans/）</summary>
internal sealed class ExpertPlanFileStore
{
    private readonly string _plansDirectory;

    public ExpertPlanFileStore(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _plansDirectory = Path.Combine(workspaceRoot, ".madorin", "plans");
        Directory.CreateDirectory(_plansDirectory);
    }

    /// <summary>保存计划到按会话隔离的文件</summary>
    public async Task SaveAsync(ExpertPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sessionDirectory = GetSessionDirectory(plan.SessionId);
        Directory.CreateDirectory(sessionDirectory);
        var fileName = $"{plan.PlanId}-{plan.Status.ToString().ToLowerInvariant()}.json";
        var filePath = Path.Combine(sessionDirectory, fileName);
        var json = JsonSerializer.Serialize(plan, RuntimeJsonContext.Default.ExpertPlan);
        await WriteAtomicallyAsync(filePath, json, ct).ConfigureAwait(false);
        await WriteAtomicallyAsync(Path.Combine(sessionDirectory, "active.json"), json, ct)
            .ConfigureAwait(false);

        foreach (var oldFile in Directory.GetFiles(sessionDirectory, $"{plan.PlanId}-*.json"))
        {
            if (!string.Equals(oldFile, filePath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(oldFile);
            }
        }
    }

    /// <summary>只恢复当前会话的活跃计划，终态计划不回退到更旧的计划</summary>
    public async Task<ExpertPlan?> LoadActiveAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var sessionDirectory = GetSessionDirectory(sessionId);
        var activeFile = Path.Combine(sessionDirectory, "active.json");
        ExpertPlan? candidate = null;
        if (File.Exists(activeFile))
        {
            try
            {
                var plan = await ReadPlanAsync(activeFile, ct).ConfigureAwait(false);
                if (plan is not null && string.Equals(plan.SessionId, sessionId, StringComparison.Ordinal))
                {
                    candidate = plan;
                }
            }
            catch (JsonException)
            {
                // Fall back to this session's plan files if the active pointer is corrupt.
            }
        }

        var latest = await LoadLatestAsync(sessionDirectory, sessionId, ct).ConfigureAwait(false);
        if (latest is not null
            && (candidate is null
                || latest.CreatedAt > candidate.CreatedAt
                || (latest.CreatedAt == candidate.CreatedAt
                    && latest.UpdatedAt > candidate.UpdatedAt)))
        {
            candidate = latest;
        }

        if (candidate is not null)
        {
            return IsActive(candidate) ? candidate : null;
        }

        if (Directory.Exists(sessionDirectory))
        {
            return null;
        }

        // Older installations used one shared active.json. Never restore another session's plan.
        var legacyFile = Path.Combine(_plansDirectory, "active.json");
        if (!File.Exists(legacyFile))
        {
            return null;
        }

        try
        {
            var legacyPlan = await ReadPlanAsync(legacyFile, ct).ConfigureAwait(false);
            return legacyPlan is not null
                && string.Equals(legacyPlan.SessionId, sessionId, StringComparison.Ordinal)
                && IsActive(legacyPlan)
                    ? legacyPlan
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<ExpertPlan?> LoadLatestAsync(
        string sessionDirectory,
        string sessionId,
        CancellationToken ct)
    {
        if (!Directory.Exists(sessionDirectory))
        {
            return null;
        }

        ExpertPlan? latest = null;
        foreach (var file in Directory.GetFiles(sessionDirectory, "plan-*.json"))
        {
            try
            {
                var plan = await ReadPlanAsync(file, ct).ConfigureAwait(false);
                if (plan is not null
                    && string.Equals(plan.SessionId, sessionId, StringComparison.Ordinal)
                    && (latest is null
                        || plan.CreatedAt > latest.CreatedAt
                        || (plan.CreatedAt == latest.CreatedAt
                            && plan.UpdatedAt > latest.UpdatedAt)))
                {
                    latest = plan;
                }
            }
            catch (JsonException)
            {
                // Ignore a damaged archive without losing other valid plans.
            }
        }

        return latest;
    }

    private string GetSessionDirectory(string sessionId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
        return Path.Combine(_plansDirectory, hash);
    }

    private static bool IsActive(ExpertPlan plan) =>
        plan.Status is ExpertPlanStatus.Pending or ExpertPlanStatus.Running;

    private static async Task<ExpertPlan?> ReadPlanAsync(string path, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.ExpertPlan);
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken ct)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, ct).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>清理历史文件（保留最近 N 个已完成的计划）</summary>
    public void CleanupOldPlans(int keepCount = 10)
    {
        var completedFiles = Directory.GetFiles(_plansDirectory, "*.json", SearchOption.AllDirectories)
            .Where(static file => file.EndsWith("-completed.json", StringComparison.Ordinal)
                || file.EndsWith("-failed.json", StringComparison.Ordinal)
                || file.EndsWith("-cancelled.json", StringComparison.Ordinal))
            .OrderByDescending(f => f)
            .Skip(keepCount)
            .ToArray();

        foreach (var file in completedFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 忽略
            }
        }
    }
}
