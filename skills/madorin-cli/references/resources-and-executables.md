# Resources And Executables

## Repository Locations

Resolve the standard executable from the skill directory. Do not select a stale repository build output as the normal CLI entry point.

| Location | Use |
| --- | --- |
| `skills/madorin-cli/bin/madorin-cli.exe` | Fixed skill executable entry point; publish output and its runtime dependencies belong in `skills/madorin-cli/bin/` |
| `src/Madorin.AI.Runtime.Cli/` | CLI project source, commands, configuration, built-in tool wiring, and project-specific publishing inputs |
| `src/Madorin.AI.Runtime.Cli/Commands/` | Command implementations; inspect when a help contract or behavior needs source-level confirmation |
| `src/Madorin.AI.Runtime.Cli/Tools/` | CLI tool-related implementation source; not a user script directory |
| `src/Madorin.AI.Runtime.Cli/bin/Debug/net10.0/madorin-cli.exe` | Development-only Debug output; do not use as the skill entry point |
| `src/Madorin.AI.Runtime.Cli/bin/Release/net10.0/win-x64/native/madorin-cli.exe` | Development-only Native AOT output; do not use as the skill entry point |
| `src/Madorin.AI.Runtime.Cli/publish/` | Project-level publish staging or packaging inputs; verify contents before use |
| `tools/` | Repository engineering utilities; inspect each tool's own documentation before executing it |
| `publish/` | Repository release and verification artifacts; do not treat every file as a distributable CLI |

Invoke the skill-local executable during normal skill use:

```powershell
.\skills\madorin-cli\bin\madorin-cli.exe --help
```

Use a source invocation only during explicit development or troubleshooting:

```powershell
dotnet run --project .\src\Madorin.AI.Runtime.Cli --no-build -- --help
```

The release pipeline owns publication into `skills/madorin-cli/bin/`. Do not make the Skill switch to any other output location. Verify the published program with `skills/madorin-cli/bin/madorin-cli.exe version --json` before relying on it.

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

Use CLI or Client SDK operations for all Runtime data. Never patch `state.db`, JSONL messages, Blobs, checkpoints, locks, or instance metadata by hand.

Each Skill has a directory named after its frontmatter `name`, a required `SKILL.md`, and optional `references/`, `scripts/`, and `assets/`. Runtime copies accepted files into a materialized catalog snapshot. A Skill script must be inside `scripts/`; it is exposed only through the authorization and environment policies and cannot bypass Grants.

## Model File Tools

Use the `mcli_fs_*` tools only inside the workspace root and the effective Grant scope.

| Operation | Tool | Required permission |
| --- | --- | --- |
| Discover or inspect | `mcli_fs_list`, `mcli_fs_stat`, `mcli_fs_read`, `mcli_fs_search` | `filesystem.read` |
| Create or update text | `mcli_fs_write`, `mcli_fs_append`, `mcli_fs_edit`, `mcli_fs_mkdir`, `mcli_fs_copy` | `filesystem.write`; overwrite additionally requires `overwrite` |
| Move or remove | `mcli_fs_move`, `mcli_fs_delete` | `filesystem.write` and `filesystem.destructive` |

For text edits, read first, preserve the returned encoding and `expectedHash`, then submit one atomic edit. Do not use `auto` when writing. Paths that contain traversal, UNC/device forms, symbolic links, directory junctions, or `.madorin` are rejected. Binary contents can be moved or copied safely but are not exposed for Base64 read/write.

## Model Memory Tools

`mcli_memory_read`, `mcli_memory_append`, `mcli_memory_update`, and `mcli_memory_delete` operate only on `global` or `project` memory, never an arbitrary file path.

Read first and carry the returned `expectedHash` for every mutation. The content takes effect on the next Agent invocation, requires approval, and shares the Runtime's write lock with file modification. Re-read on a hash conflict; do not force an overwrite.

## Model HTTP And Process Tools

| Tool | Use | Guardrails |
| --- | --- | --- |
| `mcli_http` | Send an approved HTTP request | Requires `network.access` and `NetworkPolicy`; default is public HTTPS, no redirects, no proxy, and a 1 MB response cap |
| `mcli_process` | Run an allowlisted executable | Requires `process.execute`; uses argument arrays, clears inherited environment, constrains the working directory, and captures at most 1 MB output |

For HTTP, supply an absolute `http` or `https` URL, allowed headers, and a body no larger than 1 MB. Do not expect WebSocket, gRPC, streamed responses, or automatic unrestricted redirects.

For process execution, pass an executable and argument array. Do not ask it to launch `bash`, `cmd`, `fish`, `powershell`, `pwsh`, `sh`, or `zsh`: shell execution is unconditionally rejected. For shell-based workflows, provide an explicitly authorized host tool at the appropriate permission level.
