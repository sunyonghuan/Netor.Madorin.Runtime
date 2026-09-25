# Independent CLI

## Invocation And Global Options

Use `madorin <command> [options]`. Every command supports `--workspace`, `--data-dir`, `--log-level`, `--no-color`, and `--json`.

- Use `--workspace` to select the workspace. Independent commands default to the current directory; `serve` requires it explicitly.
- Let `--data-dir` default to `<workspace>/.madorin` unless an isolated or recovery location is required.
- Use `--json` for a standard JSON envelope. For streamed task output, use `run --output-format jsonl`.
- Treat exit codes as part of the result: `0` success, `1` general or unhealthy check, `2` argument/configuration, `3` authentication, `4` workspace/data/lock, `5` live Runtime connection, `130` user cancellation.

## Command Tree

Use `--help` on any leaf to obtain the exact accepted values and defaults.

| Group | Commands | Primary use |
| --- | --- | --- |
| root | `run`, `serve`, `version`, `doctor` | Run tasks, start Runtime, verify versions, diagnose |
| config | `init`, `edit`, `show`, `validate` | Maintain standalone user configuration |
| agent | `list`, `create <agentId>`, `edit <agentId>`, `delete <agentId>` | Maintain standalone Agent definitions |
| provider | `protocols` | Inspect supported Provider protocol names |
| memory | `init <scope>`, `show <scope>`, `add <scope>`, `edit <scope>`, `clear <scope>` | Maintain global or project memory |
| skill | `list`, `show <name>`, `validate`, `refresh` | Inspect and reload discovered Skills |
| session | `list`, `show <sessionId>`, `export <sessionId>`, `archive <sessionId>`, `unarchive <sessionId>`, `compact <sessionId>`, `delete <sessionId>` | Maintain persisted Sessions |
| db | `check`, `repair`, `rebuild`, `vacuum`, `backup`, `migrate` | Check and maintain SQLite state and indexes |
| storage | `check`, `gc` | Check messages and Blob references; reclaim old unreferenced Blobs |
| ctl | `status`, `sessions`, `runs`, `cancel <runId>`, `credential update` | Control a live Runtime instance |

## First Run

```powershell
madorin config init
madorin config validate
madorin doctor --workspace <workspace>
madorin run --workspace <workspace> --input "Summarize the current task."
```

`config init` and `config edit` obtain API keys through non-echoing input. Do not use a hypothetical `--api-key` command-line option; it is deliberately unavailable.

## Task Execution

```powershell
# One input source only.
madorin run --workspace <workspace> --input "Review the repository." --mode expert

# Long input and machine-readable final result.
madorin run --workspace <workspace> --input-file .\request.md `
  --mode expert --output-format json --output-file .\result.json --no-stream

# Continue an existing Session.
madorin run --workspace <workspace> --session <sessionId> --input "Continue from the last step."
```

Rules:

- `--input` and `--input-file` are mutually exclusive.
- `--output-file` requires `--input` or `--input-file`.
- `--mode` is `expert` by default; use `meeting` or `work` only after configuring enough standalone Agents.
- Do not use `--json`, `--output-format json|jsonl`, or `--no-stream` while starting an interactive `run` without input.
- Do not combine `--json` with a non-JSON `--output-format`.

## REPL

Run `madorin` in the target workspace, or run `madorin run` without input. The first Ctrl+C cancels the active Run; a second Ctrl+C during cancellation, or Ctrl+C while idle, exits with `130`.

| Category | Slash commands |
| --- | --- |
| selection | `/model [modelId]`, `/provider [providerId]`, `/agent [agentId]`, `/mode` |
| session | `/session`, `/new`, `/resume [sessionId]`, `/history [n|all]`, `/export [path]` |
| context | `/compact`, `/context`, `/tools` |
| meeting | `/pause`, `/interrupt <text>` |
| skill | `/skill <list\|show <name>\|refresh>` |
| memory | `/memory [global|project|effective]`, `/remember <global|project>` |
| utility | `/status`, `/clear`, `/cd [path]`, `/help [command]`, `/exit`, `/quit` |

Selection changes affect the next turn only. Session mode cannot change in place; create a new Session for a different mode.

**Meeting mode**: First Ctrl+C requests cooperative pause; second Ctrl+C hard-cancels. Use `/pause` to request pause explicitly, or `/interrupt <text>` to enqueue input for the next safe point. During a paused Meeting, the next message continues the session with Host recovery first.

**Workspace switching**: Use `/cd [path]` to switch workspace in the REPL. The current session resets, but Provider/Model/Agent selection is preserved. Relative paths and `~` (home directory) are supported.

**Skill operations**: Use `/skill list`, `/skill show <name>`, or `/skill refresh` to inspect and reload Skills without leaving the REPL.

## Configuration, Agents, And Memory

```powershell
madorin agent create reviewer --name Reviewer --model gpt-5
madorin agent edit reviewer --temperature 0.2
madorin agent delete reviewer --replacement default --yes

madorin memory init global
"Prefer UTC" | madorin memory add project --workspace <workspace>
madorin memory show effective --workspace <workspace>
madorin memory clear project --workspace <workspace> --yes

madorin provider protocols --json
```

`memory` scope is `global` or `project`; only `memory show` accepts `effective`. Project memory is `<workspace>/.madorin/memory.md`; global memory is `~/.madorin/memory.md`.

## Skills And Sessions

```powershell
madorin skill validate --workspace <workspace>
madorin skill list --workspace <workspace> --json
madorin skill show madorin-cli --workspace <workspace>
madorin skill refresh --workspace <workspace>

madorin session list --workspace <workspace> --status active --limit 20
madorin session show <sessionId> --workspace <workspace> --messages
madorin session export <sessionId> --workspace <workspace> --format markdown --output .\session.md
madorin session compact <sessionId> --workspace <workspace> --dry-run
madorin session archive <sessionId> --workspace <workspace>
```

Session export accepts `markdown` (default), `txt`, or `jsonl`; include reasoning or tool calls only when explicitly required. Archive before deleting. Deletion requires `session delete <sessionId> --confirm`; omit `--include-blobs` unless the Blob ownership impact is known.

## Service And Live Control

```powershell
# The server owns the workspace write lock until it stops.
madorin serve --workspace <workspace> --instance runtime-1 --max-runs 4

# Use a separately configured terminal to inspect or control the service.
madorin ctl status --workspace <workspace> --instance runtime-1
madorin ctl runs --workspace <workspace> --session <sessionId>
madorin ctl cancel <runId> --workspace <workspace> --reason "Operator request"

# Read a replacement credential from the environment, never as a literal argument.
madorin ctl credential update --workspace <workspace> --run <runId> `
  --provider openai --api-key-env OPENAI_API_KEY
```

`ctl credential update` requires `--run`, `--provider`, and `--api-key-env`. Runtime control uses an instance binding and its handshake secret from `MADORIN_AI_RUNTIME_SECRET`; do not disclose the secret in arguments, output, or logs.

## Data Maintenance

```powershell
madorin db check --workspace <workspace> --verbose
madorin storage check --workspace <workspace> --verify-hashes
madorin db backup --workspace <workspace> --output .\backup.zip --include-messages
madorin db repair --workspace <workspace> --dry-run
madorin db rebuild --workspace <workspace> --dry-run
madorin storage gc --workspace <workspace> --dry-run --older-than 7
```

`db check`, `storage check`, Session queries, and `ctl` queries are read-only. Follow this escalation order: check, back up, dry-run, then perform the smallest confirmed repair.

| Operation | Required non-interactive confirmation |
| --- | --- |
| `doctor --fix` | `--yes` |
| `db repair` | `--yes` |
| `db rebuild` | `--confirm` |
| `storage gc` | `--confirm` |
| `session delete` | `--confirm` |

Do not combine `storage gc --dry-run` and `--confirm`. Do not delete lock files, `state.db`, or `.madorin` contents to resolve a lock or health error.

## Automation Contract

Use the standard JSON envelope and inspect both process exit code and `success`:

```json
{
  "success": true,
  "data": {},
  "warnings": [],
  "diagnosticId": null
}
```

For `run --output-format jsonl`, every line is a complete JSON object and the terminal line must independently identify success or failure. Do not merge stderr into stdout before parsing.
