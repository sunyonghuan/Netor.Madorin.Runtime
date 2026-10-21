# OpenAI 图片 CLI 审查问题修复 : 100%

依据：`../策划/技能CLI设计方案.md`。本轮修复已确认的调用、请求、产物和发布问题，不新增 Provider 或宿主集成。

## Step 1 调用生命周期与终态仲裁 : 100%
- [√] 解除空闲 stdin 对进程退出的阻塞，提前接收取消。
- [√] 统一单调截止时间、取消与唯一终态，补进程及竞态回归验证。

## Step 2 公共输入与能力契约 : 100%
- [√] 分离 JSON 输出格式与图片编码，修正 metadata 参数定义和有效对象 Schema。
- [√] 接受公共请求结构、附件和默认任务，实现宽高等价转换及文件 try-run 失败响应。

## Step 3 产物与发布一致性 : 100%
- [√] 校验图片实际格式和完整性，阻止损坏或格式不符的产物提交。
- [√] 落实发布版本、源码修订、共享摘要和 RID 合并门槛。

## Step 4 定向验收与交付 : 100%
- [√] 构建受影响项目，执行离线回归及模拟发布验证，核对实际变更范围。
- [√] 更新技能说明、记录验证结果、修改清单和未验证边界。

## 问题闭环

| 原问题 | 修复后的行为 | 对应验证 |
| --- | --- | --- |
| stdin 空闲且保持开启导致退出阻塞 | 控制读取停止等待不再依赖宿主关闭管道；有效 ID 确定后立即监听取消。 | 真实 CLI 子进程在 stdin 保持开启时退出。 |
| metadata Schema 拒绝完整有效请求、参数定义冲突 | 公共输入结构与模型有效对象分开声明；命令、认证、任务参数职责分离，补充附件入口定义。 | 离线 Schema 引擎验证有效对象；检查 argv 名唯一及可重复附件定义。 |
| 公共输出格式与图片编码混用 | `--output-format json` 固定 stdout 协议；图片编码使用 `--provider-openai-output-format`，旧文件字段走显式转换。 | 请求预览、有效参数及冲突回归。 |
| 仅验图片头导致损坏内容或错误格式落盘 | 先验证容器、实际编码格式及完整像素解码，再创建产物；移除旧的仅验头辅助类。 | PNG/JPEG/WebP、截断、损坏压缩数据及格式冒用回归。 |
| 公共附件被拒绝、task 被强制要求、公共 Schema 混入厂商限制 | 接受公共附件结构；默认任务和模型；附件先校验结构及路径，再按实际能力过滤并记录原索引。 | 公共文件、默认任务、附件越界、重复 ID、入口混用回归。 |
| width/height 未转换便被丢弃 | 成对正整数精确映射 size；缺项、非法类型、不支持尺寸或显式目标冲突均失败。 | 转换记录、缺项及冲突回归。 |
| 超时与取消终态可能相互覆盖 | 从原单调起点计算预算；截止时间先到则 timeout，已接受取消则 cancelled；响应提交时锁定终态。 | 可控时钟验证先后顺序及禁止扩大预算复活超时调用。 |
| 文件内 try_run 校验失败返回普通失败 | 尽早保留模式与有效 ID，参数错误返回 `TryRunResponse/status=invalid`；argv 显式 false 优先。 | 文件独立试运行失败及显式覆盖回归。 |
| 发布盲目保留其他 RID，缺少版本门槛 | 记录技能/契约版本、Git 修订和共享文件摘要；一致的同版本才合并 RID，升级整包替换；多 RID 全部暂存成功后再安装。 | 模拟检查覆盖同版本合并、脏源码及记录不符拒绝、升级、锁与失败保护。 |

## 验证记录

验证日期：2026-10-21。仅运行受影响项目及定向离线验证，未运行仓库全量测试。

| 实际命令 | 结果 | 覆盖范围 |
| --- | --- | --- |
| `dotnet test tests/Madorin.AI.Multimedia.Tests/Madorin.AI.Multimedia.Tests.csproj --nologo -v:minimal` | 29 项通过，0 失败，0 跳过。 | 公共协议、请求与 Schema、图片校验、假 Provider 和真实 CLI 进程退出。 |
| `./src/Multimedia/tests/publishing.tests.ps1` | 75 项检查通过。 | 临时目录、假 Git 状态和模拟构建；不代表真实 Native AOT 发布。 |
| `dotnet build src/Multimedia/Madorin.AI.OpenAI.Image/Madorin.AI.OpenAI.Image.csproj -c Release -r win-x64 --nologo -v:minimal` | 0 警告、0 错误。 | 公共库及 OpenAI CLI 的 Release/RID 构建；未进行 AOT 链接。 |

| 未验证项或交付边界 | 说明 |
| --- | --- |
| 真实 OpenAI 调用 | 未使用真实凭证、未调用外网生成，不代表模型账号权限或线上服务通过。 |
| Native AOT 与其他平台 | 未执行真实 `dotnet publish`、AOT 链接及 Linux/macOS 实机运行。图片解码新增 SkiaSharp 原生依赖，正式发布须保留对应 RID 依赖并实际验证。 |
| 正式技能包 | 未向根 `skills/` 生成正式包。发布前相关源码、技能资源及公共构建输入必须提交；脚本不会自动提交。 |
| 修改边界 | 限多媒体源码、相关测试和执行记录；未修改现有 Runtime、根解决方案或公共构建配置，未提交或推送 Git。 |

## 修改文件

| 分组 | 文件或目录 | 主要内容 |
| --- | --- | --- |
| 公共生命周期 | `src/Multimedia/Madorin.AI.Multimedia.Core/Execution/SkillInvocation.cs` | 可取消控制读取、单调预算和终态仲裁。 |
| 公共产物 | `src/Multimedia/Madorin.AI.Multimedia.Core/Files/`、公共库 `.csproj` 及 README | 完整解码验证、异步校验与原生解码依赖；删除 `ImageHeaders.cs`。 |
| 公共响应 | `src/Multimedia/Madorin.AI.Multimedia.Core/Protocol/SkillResponses.cs` | 转换记录随成功、失败及无效试运行响应返回。 |
| OpenAI 输入与输出 | `src/Multimedia/Madorin.AI.OpenAI.Image/Requests/`、`Cli/` | 文件与 argv 合并、附件结构、宽高与编码转换、试运行及统一终态出口。 |
| OpenAI 能力 | `src/Multimedia/Madorin.AI.OpenAI.Image/Metadata/`、CLI `.csproj` | Schema、参数与转换定义、显式版本和契约版本。 |
| 技能资源 | `src/Multimedia/Madorin.AI.OpenAI.Image/skill/` | 使用说明、公共请求 Schema、字段映射和第三方许可。 |
| 发布 | `src/Multimedia/Publishing.psm1`、`ReleaseValidation.ps1`、统一/单项/模板 `publish.ps1` 及 README | 发布记录、源码与摘要检查、版本门槛、多 RID 暂存和替换。 |
| 定向验证 | `tests/Madorin.AI.Multimedia.Tests/`、`src/Multimedia/tests/publishing.tests.ps1` | 新增真实图片夹具、契约、图片完整性、调用生命周期及发布回归。 |
| 执行记录 | 本文及执行目录 README | 修复状态、证据和后续验证边界。 |
