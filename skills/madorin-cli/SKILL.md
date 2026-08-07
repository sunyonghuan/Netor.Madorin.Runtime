---
name: madorin-cli
description: Use when operating, automating, diagnosing, or integrating the Madorin.AI.Runtime CLI and Runtime Client SDK, including standalone run, serve, REPL, configuration, Agents, Sessions, Skills, maintenance, live control, model tools, and host callbacks.
---

# Madorin CLI

Use the current CLI help and frozen command matrix. Do not infer commands, options, or directories from obsolete examples.

## Select An Entry Point

Use the skill-local executable as the standard entry point:

```text
<skill-root>/bin/madorin-cli.exe
```

For this repository, the resolved path is `skills/madorin-cli/bin/madorin-cli.exe`. The release process supplies this directory; invoke that executable directly whenever it exists. Do not select a repository-level Debug or Release output as the normal execution target.

Use the source invocation only for explicit development or troubleshooting when the skill-local executable has not been published:

```powershell
dotnet run --project .\src\Madorin.AI.Runtime.Cli --no-build -- <command> [options]
```

The documentation below uses `madorin` as an executable placeholder. Replace it with `skills/madorin-cli/bin/madorin-cli.exe` for normal skill use, or with the source command prefix only during development. First run `<executable> --help`; obtain local options with `<executable> <group> <command> --help`.

## Standalone CLI

1. Identify the target workspace. Pass `--workspace <path>` for workspace data; pass `--data-dir <path>` only to isolate or recover the data location.
2. For first use, run `config init`, `config validate`, and `doctor`. Pass secrets only through secure input or the prescribed environment variables, never command-line arguments.
3. Use `run --input` or `run --input-file` for one task. Omit input to enter the single-line REPL. Use `--session` to continue a Session.
4. For automation, use `--json`, or `run --output-format json|jsonl`. Parse only stdout; keep diagnostics and progress from stderr separate.
5. Start data maintenance with read-only checks or `--dry-run`. Confirm the workspace and recovery strategy before repair, rebuild, cleanup, or deletion.

Read [Independent CLI](references/independent-cli.md) for the full command tree, option combinations, REPL, and automation rules.

## Host Integration

1. Reference only `Madorin.AI.Runtime.Client` and required `Madorin.AI.Runtime.Contracts` assemblies. Do not bind a host to Server, Provider, Persistence, or Named Pipe internals.
2. Select exactly one start policy: `AttachExisting`, `StartIfMissing`, or `AlwaysStart`. When the SDK starts Runtime, do not run `serve` manually in parallel.
3. Persist and acknowledge events by `(runtimeInstanceId, gsn)`. Acknowledge only after the business projection succeeds; replay before querying Run state after reconnecting.
4. Make side-effecting tools idempotent by stable `callId`. Query an existing result after a timeout or reconnect instead of blindly executing the tool again.
5. Release the Client with `DisposeAsync`. Close only the Runtime process owned by that Client; never target other workspaces by process name.

Read [Host Integration](references/host-integration.md) for APIs, C# setup, callbacks, recovery, and release flow.

## Resources, Scripts, And Executables

- Treat this package as repository source at `skills/madorin-cli/`.
- Use `skills/madorin-cli/bin/madorin-cli.exe` as the skill's fixed executable path. The `bin/` directory is reserved for the CLI publish output and its runtime dependencies.
- Runtime discovers Skills only from `<workspace>/.madorin/skills/` and `~/.madorin/skills/`. Deploy this directory to one of those sources, then run `madorin skill validate` and `madorin skill refresh` when Runtime loading is required.
- Do not edit `<workspace>/.madorin/state.db`, `messages/`, `blobs/`, `checkpoints/`, or instance files directly. Use `session`, `db`, `storage`, `ctl`, and the Client SDK.
- File, memory, HTTP, process, and Skill scripts each require authorization. Put executable Skill content only in `scripts/`; it never bypasses Runtime policies.

Read [Resources And Executables](references/resources-and-executables.md) for source and output locations, model tools, Skill resources, and script constraints.
