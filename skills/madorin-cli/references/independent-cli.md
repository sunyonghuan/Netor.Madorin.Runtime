# Independent CLI

## Invocation And Global Options

Use `madorin <command> [options]`. Every command supports `--workspace`, `--data-dir`, `--log-level`, `--no-color`, and `--json`.

- Use `--workspace` to select the workspace. Independent commands default to the current directory; `serve` requires it explicitly.
- Let `--data-dir` default to `<workspace>/.madorin` unless an isolated or recovery location is required.
- Use `--json` for a standard JSON envelope. For streamed task output, use `run --output-format jsonl`.
- Treat exit codes as part of the result: `0` success, `1` general or unhealthy check, `2` argument/configuration, `3` authentication, `4` workspace/data/lock, `5` live Runtime connection, `130` user cancellation.

Environment variables that change behavior:

| Variable | Used by | Effect |
| --- | --- | --- |
| `MADORIN_CONFIG_DIR` | every command | Overrides the default configuration directory |
| `MADORIN_PROVIDER_DIR` | `run`, `serve` | Adds a Provider worker directory beside the CLI directory |
| `MADORIN_HOST_PID` | `serve` | The host process id; `serve` shuts down after that process exits |
| `MADORIN_AI_RUNTIME_SECRET` | `ctl` | The instance handshake secret for Runtime control |
| `MADORIN_EDITOR`, `VISUAL`, `EDITOR` | `memory edit` | The editor launched for an interactive memory edit |

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
| ctl | `status`, `sessions`, `runs`, `cancel <runId>`, `credential update`, `shutdown` | Control a live Runtime instance |

`provider protocols` reports `OpenAI`, `Anthropic`, `OpenAI Compatible`, `DeepSeek`, `Kimi`, `Aliyun`, `Azure OpenAI`, `Gemini`, `GLM`, `Custom`, and `Ollama`. Azure OpenAI and Custom require an explicit base URL and are not offered by the interactive wizard.

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

# Bound the wait and the workspace lock, and pick one Provider and model.
madorin run --workspace <workspace> --input "Check the build." `
  --provider openai --model gpt-5 --timeout 900 --lock-wait 30
```

Rules:

- `--input` and `--input-file` are mutually exclusive.
- `--output-file` requires `--input` or `--input-file`.
- `--mode` accepts `expert` or its alias `standalone`. The default is `expert`. `meeting` and `work` are removed.
- `--timeout` is a whole number of seconds for the whole run; omit it to wait indefinitely.
- `--lock-wait` is the number of seconds to wait when another Runtime holds the workspace lock. `0` fails immediately, and negative values are rejected.
- Optional request controls are `--temperature`, `--top-p`, `--top-k`, `--max-tokens`, `--frequency-penalty`, `--presence-penalty`, `--seed`, repeatable `--stop`, and `--response-format text|json`.
- Do not use `--json`, `--output-format json|jsonl`, or `--no-stream` while starting an interactive `run` without input.
- Do not combine `--json` with a non-JSON `--output-format`.

## Tool Permission Modes

Tool consent is either an explicit allowlist or an interactive policy, never both.

| Setting | Scope | Behavior |
| --- | --- | --- |
| `--allow-tools <id,id,...>` | non-interactive | Grants only the listed tool ids for the Run; workflow approvals still ask and any other tool is denied |
| `--permission-mode ask-all` | default | Every tool call and workflow approval asks for confirmation |
| `--permission-mode auto-tools` | interactive only | Tool calls are approved automatically; workflow approvals still ask |
| `--permission-mode auto-all` | interactive or non-interactive | Everything is approved automatically; the CLI prints a warning |

- `--allow-tools` cannot be combined with `auto-tools` or `auto-all`.
- `--permission-mode auto-tools` requires an interactive text session without `--output-file`.
- Inside the REPL, `/permission` switches between `ask-all`, `auto-tools`, and `auto-all` for the current CLI process only. It is unavailable when `--allow-tools` is active or when interactive consent is not available.
- Use `madorin run ... --help` and `/tools` in the REPL to see the live tool catalog rather than guessing ids.

## Workspace Instructions

`--instructions-mode <off|auto>` controls whether the workspace root `AGENTS.md` is appended to the system prompt. The default is `off`.

- The file name match is case-insensitive, and the file must sit in the workspace root.
- Inside the REPL, `/instructions` switches the mode for the current CLI process and prints the resolved file path.

## REPL

Run `madorin` in the target workspace, or run `madorin run` without input. The REPL prints a welcome banner with the version and the active Agent, Provider, Model, and Workspace, then a `madorin [<provider>/<model>]` prompt.

Input editing:

- `Enter` sends the block; `Shift+Enter` inserts a newline. Text pasted faster than the burst threshold keeps its line breaks instead of sending early.
- `LeftArrow`, `RightArrow`, `Home`, `End`, `Backspace`, and `Delete` edit the current block; `Up` and `Down` move between lines first and only walk the input history from the first or last line.
- While a Run is in flight the terminal shows a timer status line for the waiting and thinking phases instead of streaming reasoning content. Tool confirmation prompts yield the status line, and the reply area restarts the status line when it resumes.

Interrupt behavior:

1. The first Ctrl+C cancels the active Run.
2. Ctrl+C again during cancellation, or Ctrl+C while idle, exits with `130`.

| Category | Slash commands |
| --- | --- |
| help | `/help [command]`, `/help en [command]`, `/help zh [command]` |
| selection | `/model [modelId]`, `/provider [providerId]`, `/agent [agentId]`, `/mode` |
| session | `/session`, `/new`, `/resume [sessionId]`, `/history [n\|all]`, `/export [path]` |
| context | `/compact`, `/context`, `/tools` |
| policy | `/permission`, `/instructions` |
| skill | `/skill <list\|show <name>\|refresh>` |
| memory | `/memory [global\|project\|effective]`, `/remember <global\|project>` |
| utility | `/status`, `/clear`, `/cd [path]`, `/exit`, `/quit` |

- Selection changes affect the next turn only. `/mode` only displays the current mode.
- `/help` is Chinese by default. `/help en` switches the whole session to English, `/help zh` switches back, and `/help en <command>` shows one command in English. `[]` marks an optional argument and `<>` a required one.
- `/context` reports the canonical history and the effective projection as message counts plus approximate tokens formatted as `1.7k` or `0.9m`.
- `/history` defaults to the last five messages; `all` prints everything.
- `/export` writes Markdown atomically and prints to the screen when the path is omitted.
- `/pause` and `/interrupt` are removed with Meeting mode.
- Provider, model, and Agent additions are not REPL commands: leave the REPL and use `madorin config edit` or `madorin agent create`.

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

`memory` scope is `global` or `project`; only `memory show` accepts `effective`. Project memory is `<workspace>/.madorin/memory.md`; global memory is `~/.madorin/memory.md`. `memory clear` keeps the file header and needs `--yes` for a non-interactive run. `memory edit` uses `MADORIN_EDITOR`, then `VISUAL`, then `EDITOR`, and falls back to a `.`-terminated multi-line prompt.

## Skills And Sessions

```powershell
madorin skill list --workspace <workspace> --scope effective --json
madorin skill show madorin-cli --workspace <workspace>
madorin skill validate --workspace <workspace>
madorin skill refresh --workspace <workspace>

madorin session list --workspace <workspace> --status active --limit 20
madorin session list --workspace <workspace> --search review --cursor <cursor>
madorin session show <sessionId> --workspace <workspace> --messages
madorin session export <sessionId> --workspace <workspace> --format markdown --output .\session.md
madorin session compact <sessionId> --workspace <workspace> --dry-run
madorin session archive <sessionId> --workspace <workspace>
madorin session unarchive <sessionId> --workspace <workspace>
```

- `skill list|show|validate` accept `--scope effective|workspace|global`; `effective` is the default and applies the workspace-then-global precedence.
- `session list` filters by `--status active|archived|all` (default `active`), `--mode expert`, `--since <ISO 8601 with offset>`, `--search <title keyword>`, `--limit` (default 20), and `--cursor` for cursor pagination.
- Session export accepts `markdown` (default), `txt`, or `jsonl`; include reasoning or tool calls only when explicitly required.
- `session compact` accepts `--strategy full|sliding-window|summary` (default `summary`), a temporary `--provider`/`--model`, `--keep-last-tokens`, `--dry-run`, and `--force`. Dry-run never generates a summary and never writes the projection cache.
- Archive before deleting. Deletion requires `session delete <sessionId> --confirm`; omit `--include-blobs` unless the Blob ownership impact is known.

## Service And Live Control

```powershell
# The server owns the workspace write lock until it stops.
madorin serve --workspace <workspace> --instance runtime-1 --max-runs 4 --http-port 5080

# Use a separately configured terminal to inspect or control the service.
madorin ctl status --workspace <workspace> --instance runtime-1
madorin ctl sessions --workspace <workspace> --instance runtime-1
madorin ctl runs --workspace <workspace> --session <sessionId>
madorin ctl cancel <runId> --workspace <workspace> --reason "Operator request"
madorin ctl shutdown --workspace <workspace> --instance runtime-1

# Read a replacement credential from the environment, never as a literal argument.
madorin ctl credential update --workspace <workspace> --run <runId> `
  --provider openai --api-key-env OPENAI_API_KEY
```

- `serve` reads the handshake secret from redirected stdin. Never place it in an argument, the environment, or a log.
- Every `ctl` command accepts `--instance <id>` to require that the discovered Runtime instance identifier matches.
- `ctl credential update` requires `--run`, `--provider`, and `--api-key-env`, and optionally `--profile` and `--expires-at <ISO 8601 with offset>`.
- `ctl shutdown` asks Runtime to cancel active Runs, close Provider workers, and exit; the Client SDK equivalent is `ShutdownAsync`.
- When `serve` is started with `MADORIN_HOST_PID`, it also shuts down after that host process exits, so an orphaned host cannot leave Provider workers behind.
- Runtime control uses the instance binding and its handshake secret from `MADORIN_AI_RUNTIME_SECRET`; do not disclose the secret in arguments, output, or logs.

## Data Maintenance

```powershell
madorin db check --workspace <workspace> --verbose
madorin storage check --workspace <workspace> --verify-hashes
madorin db backup --workspace <workspace> --output .\backup.db --include-messages
madorin db migrate --workspace <workspace> --to 16 --dry-run
madorin db repair --workspace <workspace> --dry-run
madorin db rebuild --workspace <workspace> --dry-run
madorin db vacuum --workspace <workspace>
madorin storage gc --workspace <workspace> --dry-run --older-than 7
```

- `db backup --output` names the destination database file; `--include-messages` also writes a sibling `.messages.zip` archive of the messages directory and takes the workspace lock.
- `db migrate --to <version>` targets a schema version; omit it to apply the latest supported migration. `--dry-run` shows the plan without locking or writing.
- `db check`, `storage check`, Session queries, and `ctl` queries are read-only. Follow this escalation order: check, back up, dry-run, then perform the smallest confirmed repair.

| Operation | Required non-interactive confirmation |
| --- | --- |
| `doctor --fix` | `--yes` |
| `db repair` | `--yes` |
| `db rebuild` | `--confirm` |
| `storage gc` | `--confirm` |
| `session delete` | `--confirm` |
| `memory clear` | `--yes` |

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

Failures keep the same shape and add an error object:

```json
{
  "success": false,
  "error": {
    "code": "InvalidPermissionMode",
    "message": "--permission-mode must be ask-all, auto-tools, or auto-all.",
    "isRetryable": false,
    "diagnosticId": "..."
  },
  "warnings": [],
  "diagnosticId": "..."
}
```

For `run --output-format jsonl`, every line is a complete JSON object and the terminal line must independently identify success or failure. Do not merge stderr into stdout before parsing.