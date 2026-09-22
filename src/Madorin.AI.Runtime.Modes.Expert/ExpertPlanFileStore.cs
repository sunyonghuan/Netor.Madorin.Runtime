using System.Globalization;
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

    /// <summary>保存计划到文件（根据状态生成文件名）</summary>
    public async Task SaveAsync(ExpertPlan plan, CancellationToken ct = default)
    {
        var fileName = GenerateFileName(plan.CreatedAt, plan.Status);
        var filePath = Path.Combine(_plansDirectory, fileName);

        // 删除同一 planId 的旧状态文件
        await DeleteOldStatusFilesAsync(plan.PlanId, plan.CreatedAt, ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(plan, RuntimeJsonContext.Default.ExpertPlan);
        await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);

        // 更新 active.json 软链接（指向最新文件）
        await UpdateActiveSymlinkAsync(fileName, ct).ConfigureAwait(false);
    }

    /// <summary>加载最新的活跃计划</summary>
    public async Task<ExpertPlan?> LoadActiveAsync(CancellationToken ct = default)
    {
        var activeFile = Path.Combine(_plansDirectory, "active.json");
        if (File.Exists(activeFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(activeFile, ct).ConfigureAwait(false);
                var plan = JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.ExpertPlan);

                // 只恢复未完成的计划
                if (plan is not null && plan.Status is ExpertPlanStatus.Pending or ExpertPlanStatus.Running)
                {
                    return plan;
                }
            }
            catch (JsonException)
            {
                // 文件损坏，忽略
            }
        }

        // 回退：查找最新的 running 或 pending 文件
        return await LoadLatestUnfinishedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>查找最新的未完成计划</summary>
    private async Task<ExpertPlan?> LoadLatestUnfinishedAsync(CancellationToken ct)
    {
        var files = Directory.GetFiles(_plansDirectory, "*-running.json")
            .Concat(Directory.GetFiles(_plansDirectory, "*-pending.json"))
            .OrderByDescending(f => f)
            .ToArray();

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var plan = JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.ExpertPlan);
                if (plan is not null)
                {
                    return plan;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    /// <summary>生成文件名：yyyyMMdd-HHmmss-{status}.json</summary>
    private static string GenerateFileName(DateTimeOffset createdAt, ExpertPlanStatus status)
    {
        var timestamp = createdAt.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var statusStr = status.ToString().ToLowerInvariant();
        return $"{timestamp}-{statusStr}.json";
    }

    /// <summary>删除同一计划的旧状态文件</summary>
    private async Task DeleteOldStatusFilesAsync(
        string planId,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var timestamp = createdAt.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var pattern = $"{timestamp}-*.json";
        var oldFiles = Directory.GetFiles(_plansDirectory, pattern);

        foreach (var oldFile in oldFiles)
        {
            try
            {
                File.Delete(oldFile);
            }
            catch (IOException)
            {
                // 文件被占用，忽略
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>更新 active.json（直接复制内容，Windows 不支持软链接）</summary>
    private async Task UpdateActiveSymlinkAsync(string targetFileName, CancellationToken ct)
    {
        var activeFile = Path.Combine(_plansDirectory, "active.json");
        var targetFile = Path.Combine(_plansDirectory, targetFileName);

        if (File.Exists(targetFile))
        {
            var content = await File.ReadAllTextAsync(targetFile, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(activeFile, content, ct).ConfigureAwait(false);
        }
    }

    /// <summary>清理历史文件（保留最近 N 个已完成的计划）</summary>
    public void CleanupOldPlans(int keepCount = 10)
    {
        var completedFiles = Directory.GetFiles(_plansDirectory, "*-completed.json")
            .Concat(Directory.GetFiles(_plansDirectory, "*-failed.json"))
            .Concat(Directory.GetFiles(_plansDirectory, "*-cancelled.json"))
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
