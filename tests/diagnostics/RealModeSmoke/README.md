# 三模式真实冒烟测试

本目录通过公开 Client SDK、Named Pipes 和 Runtime Server 调用全局配置中的真实模型，并执行测试工作区内的真实文件工具。不会返回伪造的模型或工具结果。

先构建 CLI Debug，并构建所需 Provider Worker（至少 `madorin-provider-deepseek`）。冒烟进程内启动 RuntimeServer，会按 `ProcessPath` 同目录查找 `madorin-provider-*.exe`；`RealModeSmoke.csproj` 会在 Build 后把各 Worker 输出拷到冒烟目录。也可设置 `MADORIN_PROVIDER_DIR` 指向含 Worker 的发布 `bin/`。

然后运行 `dotnet run --project tests/diagnostics/RealModeSmoke -- <输出目录> [场景]`。基础场景为 expert、work、meeting；省略则运行全部。定向场景包括 timed、work-confirm、work-execute、work-interrupt、work-revision、work-set-plan、work-ask-pending、meeting-strict、meeting-controlled、meeting-interrupt。每次复测使用新的输出目录。

`work-set-plan` 强制总经理调用 `mcli_work_set_plan` 发布计划（开启 RequirePlanConfirmation），再确认执行；用于验收工具化计划路径，不能用裸 JSON 文本计划代替。

`work-ask-pending` 强制总经理先 `mcli_check_pending_user_input` 再 `mcli_ask_user`；宿主把答案放在 `ApprovalResponse.Reason`，用于验收共享交互工具。

模型默认使用全局 defaultProvider/defaultModel，可通过 `SMOKE_PROVIDER`、`SMOKE_MODEL` 选择全局配置中已有条目。凭据只在内存加载，不写入报告。

输出目录包含每轮输入、SDK 事件 JSONL、会话快照和真实工具产物。工具授权覆盖输出目录下 workspace 的文件读写及 dotnet 进程；预设进程用例只运行 --version 或本测试宿主的 --timed-read（读取测试 CSV 并记录时间）。工作计划批准和会议结束确认分别观察，不能用工具授权代替。

`meeting-strict` 约束主持输出裸 JSON；`meeting-controlled` 明确区分主持调度和 Summarizer；`meeting-interrupt` 在参会者运行中尝试通过现有 SDK 提交补充，记录拒绝或接受，再测取消后的同会话继续。`work-revision` 显式指定两个计划版本字段，仅作为底层修订能力对照，不能当作自然对话重规划通过。

Windows PowerShell 5.1 可用 UTF-8 显式读取方式生成逐轮报告：

```powershell
& ([scriptblock]::Create([IO.File]::ReadAllText(
    (Join-Path (Get-Location) 'diagnostics/RealModeSmoke/Summarize-Smoke.ps1'))))
```

记录器使用共享文件访问，并检测事件读取任务失败；汇总脚本同样使用共享读取。工具失败后恢复成功会保留失败事件，不将终态完成解释为全部工具首次成功。

测试宿主直接引用刚构建的 CLI 输出程序集，避免修改生产项目；并非独立发布项目。大文件分块维护，生产代码保持测试开始时的提交状态。
