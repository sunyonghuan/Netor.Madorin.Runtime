# 多媒体技能发布模板

此目录是模板，不是已实现的 CLI，不会出现在统一发布脚本的 `list` 中。

1. 将本目录中的发布文件和 `skill/` 复制到 `src/Multimedia/<项目名>/`，不要将本 README 作为技能文档发布。
2. 把 `publish.json.example` 改名为 `publish.json`，把 `skill/SKILL.md.template` 改名为 `skill/SKILL.md`。
3. 修改清单的项目路径、技能名、可执行文件名和实际支持的 RID；CLI 项目必须是 `net10.0`、`OutputType=Exe` 的可执行项目，支持 Native AOT，并在不带条件的项目 `PropertyGroup` 中显式声明匹配的 `<AssemblyName>skill-...</AssemblyName>`。
4. 填写真实技能说明和 Provider 协议字段引用；说明通过 `scripts/invoke.ps1` 或 `scripts/invoke.sh` 运行，CLI 位于 `runtime/<RID>/`。
5. 保留项目的 `publish.ps1`，必要的辅助脚本放进 `skill/scripts/`；`invoke.ps1` 和 `invoke.sh` 是打包器生成的保留名称，不在源码技能目录重复提供。
6. 先运行统一入口的 `list` 核对登记，再执行单项发布；成功包位于仓库根的 `skills/<技能名>/`。

项目还需显式声明 `<Version>0.1.0</Version>` 和 `<SkillContractVersion>1.0</SkillContractVersion>`，CLI 的版本输出须一致。正式发布前提交相关源码与技能文件；同版本合并会核对 Git 修订及共享摘要。升级时可使用 `-Rid win-x64,linux-x64` 一次暂存多个平台，不沿用旧版本程序。

`skill/scripts/show-metadata.ps1` 是无网络查询辅助脚本，只有 CLI 实际实现 `metadata` 后才可运行。模板不调用供应商、不携带账号或密钥。
