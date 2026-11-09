---
name: madorin-cli
description: Use when operating, automating, diagnosing, or integrating the Madorin.AI.Runtime CLI and Runtime Client SDK, including standalone run, serve, REPL, configuration, Agents, Sessions, Skills, the MCP bridge, maintenance, live control, permission modes, model tools, and host callbacks.
---

# Madorin CLI

Use the current CLI help and frozen command matrix. Do not infer commands, options, or directories from obsolete examples.

## Select An Entry Point

Resolve the executable in this order:

1. `releases/Madorin-Cli/<rid>/madorin-cli/bin/madorin-cli` (`.exe` on Windows) when `version --json` matches `MadorinCliVersion` in `Directory.Build.props`.
2. `<skill-root>/bin/madorin-cli.exe` when that packaged binary is the same version.
3. `dotnet run --project .\src\Madorin.AI.Runtime.Cli --no-build -- <command>` only while developing and no current publish exists.

Current publish RIDs are `win-x64`, `linux-x64`, and `osx-arm64`. Publish with `build/madorin-cli.publish.ps1`; `-Version <x.y.z>` pins the version, and `win`, `linux`, `mac`, `list`, `-Rebuild`, `-NoBump`, `-SkipProviders`, and `-Bump` control the run. Provider workers must sit in the same directory as the CLI. Do not use a Debug build, a project `bin/Release` output, or an older `skills/madorin-cli/bin` snapshot when a matching platform publish exists; that snapshot can lag by many versions.

The only runtime mode is `expert`. `standalone` is an alias. `meeting` and `work` are removed.

The documentation below uses `madorin` as an executable placeholder. Replace it with the resolved executable. First run `<executable> --help`; obtain local options with `<executable> <group> <command> --help`.

## Standalone CLI

1. Identify the target workspace. Pass `--workspace <path>` for workspace data; pass `--data-dir <path>` only to isolate or recover the data location.
2. For first use, run `config init`, `config validate`, and `doctor`. Pass secrets only through secure input or the prescribed environment variables, never command-line arguments.
3. Use `run --input` or `run --input-file` for one task. Omit input to enter the interactive REPL. Use `--session` to continue a Session.
4. For automation, use `--json`, or `run --output-format json|jsonl`. Parse only stdout; keep diagnostics and progress from stderr separate.
5. Decide tool authorization explicitly. `--allow-tools <ids>` is a non-interactive allowlist; `--permission-mode ask-all|auto-tools|auto-all` selects the interactive policy. The two cannot be combined, and `auto-tools` requires an interactive text session without `--output-file`.
6. Add `--instructions-mode auto` only when the workspace root `AGENTS.md` should be appended to the system prompt. The default is `off`.
7. Start data maintenance with read-only checks or `--dry-run`. Confirm the workspace and recovery strategy before repair, rebuild, cleanup, or deletion.

Read [Independent CLI](references/independent-cli.md) for the full command tree, option combinations, REPL keys and slash commands, permission modes, and automation rules.

## Interactive REPL

Running the executable with no arguments, or `run` without input, opens the REPL with the welcome banner, the active Agent/Provider/Model/Workspace, and a status line that tracks waiting and thinking time.

- `Enter` sends, `Shift+Enter` inserts a newline, and a pasted burst keeps its newlines. Arrow keys, `Home`, `End`, `Backspace`, and `Delete` edit the current block; `Up`/`Down` walk the input history only after the cursor reaches the first or last line.
- `/help` is Chinese by default; `/help en` switches to English for the rest of the process, `/help zh` switches back, and `/help en <command>` shows one command in English.
- `/permission` and `/instructions` change the tool permission mode and the `AGENTS.md` auto-load switch for the current CLI process only. Both reset to their defaults when the process exits.
- Selection commands (`/model`, `/provider`, `/agent`) only affect the next turn. `/cd` switches workspace and resets the Session while keeping the selection.

## Host Integration

1. Reference only `Madorin.AI.Runtime.Client` and required `Madorin.AI.Runtime.Contracts` assemblies. Do not bind a host to Server, Provider, Persistence, or Named Pipe internals.
2. Select exactly one start policy: `AttachExisting`, `StartIfMissing`, or `AlwaysStart`. When the SDK starts Runtime, do not run `serve` manually in parallel.
3. Persist and acknowledge events by `(runtimeInstanceId, gsn)`. Acknowledge only after the business projection succeeds; replay before querying Run state after reconnecting.
4. Make side-effecting tools idempotent by stable `callId`. Query an existing result after a timeout or reconnect instead of blindly executing the tool again.
5. Close with `ShutdownAsync` when the host owns the Runtime, then `DisposeAsync`. `ShutdownAsync` cancels active Runs, closes Provider workers, and stops the process through `runtime.shutdown`. `OwnedProcessClosePolicy` only governs the process this Client started.

Read [Host Integration](references/host-integration.md) for APIs, C# setup, callbacks, round-based history, shutdown, recovery, and release flow.

## MCP Bridge

The MCP bridge is a command-line client, not a native Runtime tool. It needs `node` on `PATH` and uses only Node built-ins. The engine is `<skill-root>/mcp/bin/mcp-bridge.mjs`. Call a platform entry below; do not invoke the `.mjs` file directly.

| Platform | Entry | Quoting |
| --- | --- | --- |
| Windows PowerShell | `<skill-root>/bin/mcp.ps1` | Preserves JSON quotes. Prefer this in PowerShell. |
| Windows cmd.exe | `<skill-root>/bin/mcp.cmd` | Inner quotes are lost. Use `--arg key=value` or `--args-file`. |
| Linux | `<skill-root>/bin/mcp.sh` | `"$@"` preserves arguments. |
| macOS | `<skill-root>/bin/mcp.zsh` | `"$@"` preserves arguments. |

All four entries run `node` on the same bridge and return its exit code. If `mcp-bridge.mjs` is missing, they write `Error: bridge not found` to stderr and exit `2`.

Server definitions live in `<skill-root>/mcp/servers.json`. That file and `mcp/catalog/` are tracked. `mcp/logs/` stays untracked. Secrets belong in `${env:NAME}`, never in `servers.json`, the catalog, or global memory. Full command and server notes are in [mcp/README.md](mcp/README.md).

```text
mcp list
mcp tools <server>
mcp schema <server> [tool]
mcp call <server> <tool> --args '<json>'
mcp call <server> <tool> --arg key=value
mcp index
mcp doctor [server]
mcp show <server>
```

After a new MCP server or tool is obtained, append its name and the exact call pattern to the user global memory `~/.madorin/memory.md` (Windows: `%USERPROFILE%\.madorin\memory.md`). Later Agent sessions load that file automatically. Do not store tokens, passwords, or other secrets there. Non-obvious rules, such as required ids or bootstrap order, belong in that same memory section; do not leave them only in the current conversation.

## Resources, Scripts, And Executables

- Treat this package as repository source at `skills/madorin-cli/`.
- Prefer `releases/Madorin-Cli/<rid>/madorin-cli/bin/` for the current CLI and Provider workers. `skills/madorin-cli/bin/` is the packaged Windows snapshot and can lag the source version.
- Runtime discovers Skills only from `<workspace>/.madorin/skills/` and `~/.madorin/skills/`. Deploy this directory to one of those sources, then run `madorin skill validate` and `madorin skill refresh` when Runtime loading is required.
- Do not edit `<workspace>/.madorin/state.db`, `messages/`, `blobs/`, `checkpoints/`, or instance files directly. Use `session`, `db`, `storage`, `ctl`, and the Client SDK.
- File, memory, command, HTTP, and Skill script tools each require authorization. `mcli_command` runs through the host shell but is authorized by its normalized starting directory, not by its script text. Put executable Skill content only in `scripts/`; it never bypasses Runtime policies.

Read [Resources And Executables](references/resources-and-executables.md) for source and output locations, model tools, Skill resources, environment variables, and script constraints.
