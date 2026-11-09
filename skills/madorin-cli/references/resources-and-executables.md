# Resources And Executables

## Repository Locations

Resolve the standard executable from the skill directory. Do not select a stale repository build output as the normal CLI entry point.

| Location | Use |
| --- | --- |
| `releases/Madorin-Cli/<rid>/madorin-cli/bin/` | Current platform publish. RIDs are `win-x64`, `linux-x64`, and `osx-arm64`. CLI, Provider workers, `providers.manifest.json`, and the SQLite native library belong here |
| `skills/madorin-cli/bin/madorin-cli.exe` | Packaged Windows skill snapshot. Use it only when its `version` matches `MadorinCliVersion` |
| `src/Madorin.AI.Runtime.Cli/` | CLI project source, commands, configuration, built-in tool wiring, and project-specific publishing inputs |
| `src/Madorin.AI.Runtime.Cli/Commands/` | Command implementations; inspect when a help contract or behavior needs source-level confirmation |
| `src/Madorin.AI.Runtime.Cli/Config/` | Standalone configuration, wizard, terminal styling, logo, and interactive line editor |
| `src/Madorin.AI.Runtime.Cli/Tools/` | CLI tool-related implementation source; not a user script directory |
| `src/Madorin.AI.Runtime.Tools.Builtin/` | Built-in model tool descriptors and executors, including `mcli_command` |
| `src/Madorin.AI.Runtime.Cli/bin/` | Development build output; do not use as the skill entry point |
| `build/madorin-cli.publish.ps1` | Publish entry. No argument publishes all three platforms; `win`, `linux`, `mac`, and `list` select or print platforms |
| `tools/` | Repository engineering utilities; inspect each tool's own documentation before executing it |

Invoke the current platform publish during normal repository use:

```powershell
.\releases\Madorin-Cli\win-x64\madorin-cli\bin\madorin-cli.exe version --json
```

Use a source invocation only during explicit development or troubleshooting:

```powershell
dotnet run --project .\src\Madorin.AI.Runtime.Cli --no-build -- --help
```

`build/madorin-cli.publish.ps1` writes each platform under `releases/Madorin-Cli/<rid>/`. It does not refresh `skills/madorin-cli/bin/`. Useful switches:

| Switch | Effect |
| --- | --- |
| `-Version <x.y.z>` | Publish an exact version and write it into `Directory.Build.props` and `Directory.Packages.props` |
| `-Bump major\|minor\|patch` | Choose the increment when no explicit version is given; `patch` is the default |
| `-NoBump` | Keep the current version |
| `-Rebuild` | Force a clean rebuild |
| `-SkipProviders` | Publish the CLI without the Provider workers |
| `-Rid <rid>` | Publish one runtime identifier |

Verify the chosen executable with `version --json` before relying on it. Provider workers are loaded from the CLI directory, or from `MADORIN_PROVIDER_DIR` when that variable is set.

## Environment Variables

| Variable | Consumer | Effect |
| --- | --- | --- |
| `MADORIN_CONFIG_DIR` | CLI | Overrides the default configuration directory |
| `MADORIN_PROVIDER_DIR` | CLI, Runtime | Extra Provider worker directory |
| `MADORIN_HOST_PID` | `serve` | Parent host process id; `serve` cancels and shuts down after it exits |
| `MADORIN_AI_RUNTIME_SECRET` | `ctl` | Instance handshake secret for live control |
| `MADORIN_EDITOR`, `VISUAL`, `EDITOR` | `memory edit` | Editor precedence for an interactive memory edit |

`MADORIN_HOST_PID` is set automatically by the Client SDK when it starts Runtime, so a host crash cannot leave Provider workers running. A manually started `serve` does not set it and is never closed by it.

## Runtime Data And Skill Sources

| Location | Purpose |
| --- | --- |
| `~/.madorin/config.json` | Standalone user Provider, model, and default Agent configuration |
| `~/.madorin/agents/` | Standalone Agent definitions |
| `~/.madorin/memory.md` | Global memory |
| `<workspace>/.madorin/config.json` | Optional workspace Selection configuration |
| `<workspace>/.madorin/memory.md` | Project memory |
| `<workspace>/.madorin/state.db` | Runtime state and indexes |
| `<workspace>/.madorin/messages/` | Canonical message history |
| `<workspace>/.madorin/blobs/` | Content-addressed large content |
| `<workspace>/.madorin/checkpoints/`, `replay/`, `logs/` | Recovery, replay, and diagnostics |
| `<workspace>/.madorin/skills/` | Workspace Skill source with highest automatic precedence |
| `~/.madorin/skills/` | User-global automatic Skill source |
| `<workspace>/AGENTS.md` | Optional workspace instructions, loaded only with `--instructions-mode auto` or `/instructions` |

Use CLI or Client SDK operations for all Runtime data. Never patch `state.db`, JSONL messages, Blobs, checkpoints, locks, or instance metadata by hand.

Each Skill has a directory named after its frontmatter `name`, a required `SKILL.md`, and optional `references/`, `scripts/`, and `assets/`. Runtime copies accepted files into a materialized catalog snapshot. A Skill script must be inside `scripts/`; it is exposed only through the authorization and environment policies and cannot bypass Grants.

## Model File Tools

Use the `mcli_fs_*` tools only inside the workspace root and the effective Grant scope.

| Operation | Tool | Required permission |
| --- | --- | --- |
| Discover or inspect | `mcli_fs_inspect` (`mode=list|stat`), `mcli_fs_read`, `mcli_fs_search` | `filesystem.read` |
| Attach binary content | `mcli_fs_attach` | `filesystem.read`; returns metadata plus a separate Blob instead of text |
| Create or update text | `mcli_fs_write` (`create`/`overwrite`/`append`/`mkdir`), `mcli_fs_edit`, `mcli_fs_copy` | `filesystem.write`; overwrite/append additionally require `overwrite` |
| Move or remove | `mcli_fs_move`, `mcli_fs_delete` | `filesystem.write` and `filesystem.destructive` |

The pre-3.0 aliases `mcli_fs_list`, `mcli_fs_stat`, `mcli_fs_append`, and `mcli_fs_mkdir` still execute for compatibility but carry the internal tag and are no longer advertised; prefer `mcli_fs_inspect` and `mcli_fs_write` instead.

For text edits, read first, preserve the returned encoding and `expectedHash`, then submit one atomic edit. `mcli_fs_edit` accepts 1 to 100 operations of `replace`, `delete`, `insertBefore`, or `insertAfter` and rejects a stale hash. Do not use `auto` when writing. Paths that contain traversal, UNC/device forms, symbolic links, directory junctions, or `.madorin` are rejected. Binary contents can be moved or copied safely, and `mcli_fs_attach` carries them without Base64 decoding; there is no Base64 text read or write.

`mcli_fs_search` pages results: `page` starts at 1, `pageSize` or `maxResults` sets the page length (1 to 100, default 20), and the result reports `matches`, `total`, `page`, `pageSize`, and `hasMore`. Request the next page only when `hasMore` is true.

## Model Memory Tools

`mcli_memory_read` and `mcli_memory_write` (`operation=append|update|delete`) operate only on `global` or `project` memory, never an arbitrary file path. The same mutations remain callable through the internal aliases `mcli_memory_append`, `mcli_memory_update`, and `mcli_memory_delete`, which are not advertised to the model.

Read first and carry the returned `expectedHash` for every mutation. The content takes effect on the next Agent invocation, requires approval, and shares the Runtime's write lock with file modification. Re-read on a hash conflict; do not force an overwrite.

## Model Command And HTTP Tools

| Tool | Use | Guardrails |
| --- | --- | --- |
| `mcli_command` | Run shell commands and programs (`git`, `dotnet`, `python`, installed CLIs) through the host shell | Requires `process.execute`; authorized by the normalized `workingDirectory`, not by script text; script is capped at 8 KiB UTF-8 |
| `mcli_http` | Send an approved HTTP request | Requires `network.access` and `NetworkPolicy`; default is public HTTPS, no redirects, no proxy, and a 1 MB response cap |

`mcli_command` replaces the removed `mcli_process` tool. Arguments:

| Field | Meaning |
| --- | --- |
| `script` | Required. One command or a multi-line script; commands separated by newlines share one shell process |
| `shell` | `auto`, `pwsh`, `powershell`, `cmd`, `bash`, `zsh`, or `sh`. `auto` picks `pwsh` then `powershell` then `cmd` on Windows, and `bash` then `zsh` then `sh` elsewhere |
| `workingDirectory` | Initial directory; defaults to the workspace root and must match the authorized Grant directory |
| `environment` | String overrides layered over the inherited CLI environment |
| `stdin` | Up to 1 MiB UTF-8 for programs that read standard input |
| `encoding` | `utf-8` (default), `gbk`, or `gb18030`; applies to stdin, stdout, and stderr |

The tool returns `shell`, `exitCode`, `stdout`, `stderr`, `stdoutTruncated`, and `stderrTruncated`. Keep the script under 8 KiB: write long content or scripts with `mcli_fs_write` and pass a file path, or send it through `stdin`. Never retry a permission denial.

For HTTP, supply an absolute `http` or `https` URL, allowed headers, and a body no larger than 1 MB. Do not expect WebSocket, gRPC, streamed responses, or automatic unrestricted redirects.

## Model Skill And Expert Tools

- Skill content is reached through `load_skill` (`runtime.skill.read`), `read_skill_resource`, and `run_skill_script`, which read and execute against the immutable Skill snapshot. Scripts require `skill.script` approval; Runtime user permissions still apply, so a Skill cannot widen its own access.
- Expert mode exposes `mcli_delegate_agent`, `mcli_agent_job_create`, `mcli_agent_job_create_many`, `mcli_agent_job_status`, `mcli_agent_job_result`, `mcli_agent_job_wait`, `mcli_agent_job_cancel`, plus the plan tools `mcli_expert_create_plan`, `mcli_expert_execute_step`, `mcli_expert_get_plan`, `mcli_expert_update_step_status`, and `mcli_expert_abandon_plan`. All are limited to Expert mode.
- Use the live tool catalog from the Runtime instead of inventing tool ids; `madorin run ... --help` and the REPL `/tools` command show what is actually advertised.
