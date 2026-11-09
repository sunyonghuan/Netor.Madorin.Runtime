# Madorin CLI 0.1.104

上一份已发布版本是 `0.1.103`，对应 `dist` 中已有的 Windows、Linux 和 macOS 安装包。本次发布日志沿用既有发布编号 `0.1.104`；当前源码内部版本已继续递增到 `0.1.106`，不改变本次 `dist` 发布编号。

## 版本信息

- 上一发布版本：`0.1.103`
- 本次发布日志版本：`0.1.104`
- 本次截止提交：`5b298ba`
- 完整提交号：`5b298ba568d7ddeccdb790035126ad561111949d`
- 发布日期：`2026-11-09`
- 本次没有重新打包安装产物；`dist` 中现有压缩包仍为 `0.1.103` 产物。

## 文档与命令契约同步

- 完成 `docs/使用文档/` 与当前 CLI 源码、实际 `--help` 输出和命令矩阵的交叉核对。
- 补充 `provider protocols`、`skill list/show/validate/refresh`、`ctl shutdown`、`doctor --fix`、`skill --scope`、`db check --verbose` 等使用说明。
- 补充 `session compact --provider/--model`、`db migrate --to`、`madorin --version` 和 `--mode standalone` 兼容别名说明。
- 修正独立 CLI 配置 JSON 示例，使其符合 `StandaloneConfig`；补充 `Custom` Provider 协议和发布包文件名说明。
- 修正命令矩阵的 CLI、协议和 SQLite Schema 版本说明，并将机器快照同步到当前 54 条命令路径。

## 已验证内容

- `Stage7CommandMatrixTests`：11/11 通过。
- 命令矩阵与机器快照：54 条路径，差集为空。
- REPL 行为核对：首次 Ctrl+C 取消当前 Run，再次 Ctrl+C 退出；有 Session ID 时显示英文恢复提示，包含程序内 `/resume <session-id>` 和退出后的 `madorin run --session <session-id>`。
- 状态显示文档覆盖 `working`、`thinking`、`tool:<toolname>` 和 `0.00k` 上下文大小格式。

## 产物

本次只更新文档、命令契约快照和发布记录，没有覆盖 `dist` 中已有的压缩包或校验文件。
