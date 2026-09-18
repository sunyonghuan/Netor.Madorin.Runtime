# Provider Worker 图标与版本对齐 : 100%

## Step 1 准备真实 Provider ICO : 100%
- [√] 从 Lobe Icons 取 9 个厂商彩色 Logo，OpenAI Compatible 用同款 blossom 青绿底区分
- [√] 转成 Windows ICO 写入各 Worker `assets/provider.ico`

## Step 2 元数据与 CLI 对齐 : 100%
- [√] 产品名 / 公司 / 版权 / 版本集中到 `Directory.Build.props`（`MadorinCliVersion`）
- [√] Worker 按 CLI 格式写 AssemblyTitle、FileDescription、Product、Description、Version
- [√] CLI 改读共享版本，exe 元数据值不变

## Step 3 发布脚本升版同步 : 100%
- [√] `madorin-cli.publish.ps1` 升 `MadorinCliVersion`，publish 传入 Version
- [√] `madorin-cli.publish.linux.sh` 同步（macos.sh 走同一脚本）

## Step 4 编译验证 : 100%
- [√] Ollama Worker Debug 生成成功，0 警告
- [√] 10 个 Worker `Version=0.1.46`、`Product=madorin-cli`；exe FileVersionInfo 对齐

## Step 5 AOT 验收（图标与版本合入后） : 100%
- [√] win-x64 Native AOT 再发通：`build/madorin-cli.publish.ps1 -Rid win-x64 -NoBump`（531s，exit 0，版本仍 0.1.46）
- [√] bin：CLI 28.19 MiB + 10 Worker + manifest + `e_sqlite3.dll`；CLI ILC 不 root `Microsoft.Agents.AI.OpenAI`；OpenAI Worker 仍 Full
- [√] 11 个 AOT exe 均为 `Version=0.1.46`、`Product=madorin-cli`；FileDescription = AssemblyName
- [√] 从 AOT exe 抽出关联图标，10 个 Worker 均为对应厂商标
- [√] `--help` / `--json version` 0.1.46 / 11 协议；Compatible 空 Key doctor：`providers[0].apiKey: A value is required.`

## 本轮修改的文件

- `Directory.Build.props`
- `src/Madorin.AI.Runtime.Cli/Madorin.AI.Runtime.Cli.csproj`
- `src/Providers/ProviderWorker.props`
- `src/Providers/Madorin.AI.Runtime.Providers.*.Worker/*.csproj`（10）
- `src/Providers/Madorin.AI.Runtime.Providers.*.Worker/assets/provider.ico`（10）
- `build/madorin-cli.publish.ps1`
- `build/madorin-cli.publish.linux.sh`

