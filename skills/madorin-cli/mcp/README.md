# MCP Bridge

Zero-dependency MCP client bridge for the Madorin CLI environment.
Uses only Node built-ins (`child_process`, `fs`, `path`, `url`); no npm packages.

## Layout

| Path | Purpose | Typical size |
| --- | --- | --- |
| `servers.json` | Single source of truth for server definitions. Supports `${env:NAME}`. Tracked; never commit tokens, passwords, or other secrets. | < 2 KB |
| `bin/mcp-bridge.mjs` | Core JSON-RPC engine: stdio (newline-delimited) and streamable HTTP (JSON or SSE). | ~29 KB |
| `catalog/index.json` | **Compact index**: per server status, protocol, tool count, shard pointer. No tool schemas. | ~1 KB |
| `catalog/index.md` | Human-readable index listing tool names per server. | few KB |
| `catalog/servers/<server>.json` | **Per-server shard** holding that server's full tool schemas. Loaded on demand only. | grows with one server |
| `../bin/mcp.ps1` | PowerShell entry (preserves JSON quoting, resolved first in PowerShell). | |
| `../bin/mcp.cmd` | cmd.exe entry (use `--arg key=value` to avoid quote mangling). | |
| `../bin/mcp.sh` | Linux bash entry. Forwards `"$@"` to Node. | |
| `../bin/mcp.zsh` | macOS zsh entry. Forwards `"$@"` to Node. | |

### Why shards instead of one catalog file

A single catalog file scales badly: one server with 29 tools is 45 KB, so several servers with 100 tools each would be megabytes, and reading one tool signature would mean loading all of it. Sharding keeps the always-read index at ~1 KB, while a shard is only parsed when you ask for a schema. Verified: an 800-tool shard (394 KB) resolves a single tool in ~50 ms.

`mcp index` writes one shard per server and rewrites `index.json` / `index.md`. It never merges shards, so adding a server costs one new file.

`servers.json`, `catalog/`, and `logs/` are machine-local and listed in the repository `.gitignore`; only the bridge, entries, and this README are tracked. On a fresh clone, create `servers.json` from the example below and run `mcp index`.

## Usage

```
mcp list                        # servers + connectivity + tool counts
mcp tools <server>              # live tools/list with argument signatures
mcp schema <server>             # tool names + descriptions from the shard
mcp schema <server> <tool>      # full input schema for one tool (no server contact)
mcp schema --tool <tool>        # find a schema by tool name across all shards
mcp call <server> <tool> --args '<json>'
mcp call <server> <tool> --args-file ./args.json
mcp call <server> <tool> --arg k=v --arg n=3     # cmd.exe safe, repeatable
mcp index                       # rebuild index + per-server shards
mcp doctor [server]             # env, executable, initialize, tools/list, ping
mcp show <server>               # resolved definition with placeholders expanded
```

Flags: `--json`, `--quiet`, `--no-probe`, `--help`, `--version`.

## Adding a server

1. Append an entry under `mcpServers` in `servers.json`. stdio uses `command` + `args`; http uses `url` + `headers`. Put secrets in `${env:NAME}` and set the variable at user scope.
2. `mcp doctor <server>` to verify env, transport, handshake, and tool listing.
3. `mcp index` to rewrite the index and that server's shard.
4. Record the new server or tool name, its call pattern, and any non-obvious usage rule in global memory `~/.madorin/memory.md` (bootstrap order, required ids, timing). Later sessions load that file automatically. Do not write secrets there.

```json
{
  "mcpServers": {
    "stdio-example": {
      "transport": "stdio",
      "command": "C:\\path\\server.exe",
      "args": [],
      "env": { "TOKEN": "${env:MCP_TOKEN}" },
      "timeoutMs": 180000,
      "description": "what it does"
    },
    "http-example": {
      "transport": "http",
      "url": "https://example.com/mcp",
      "headers": { "Authorization": "Bearer ${env:MCP_TOKEN}" }
    }
  }
}
```

`type: "command"` is an alias for `transport: "stdio"`. Set `"disabled": true` to keep an entry without indexing it.

## Quoting rules

- **PowerShell**: `mcp call s t --args '{"a":"b"}'` works; `mcp.ps1` is resolved first.
- **cmd.exe**: quotes are destroyed by the batch layer. Use `--arg a=b` or `--args-file`.
- **Linux**: run `mcp.sh`; arguments are forwarded unchanged.
- **macOS**: run `mcp.zsh`; arguments are forwarded unchanged.
- **Any shell**: `--args '@stdin'` reads JSON from stdin.

## Registered servers

### charp_code_map (stdio)

charpcodemap 3.0.3, protocol 2025-03-26, 29 tools. C#/C++ semantic index and navigation.

Per-repository bootstrap (run once, then always pass the same `workspace_id`):

```
mcp call charp_code_map md_cs_mp_index_ensure_baseline --arg repo_path=<abs repo>
mcp call charp_code_map md_cs_mp_workspace_create       --arg repo_path=<abs repo> --arg workspace_id=<repo-unique id>
```

After editing any C# file, refresh the overlay or queries will not see your changes:

```
mcp call charp_code_map md_cs_mp_index_refresh_overlay --arg repo_path=<abs repo> --arg workspace_id=<id>
```

Notes:
- `workspace_id` must be unique per repository. A stale manifest with the same name causes `STORAGE_FORMAT_INVALID`, so do not blindly reuse the `session` value from the server's own guide.
- Implements only `initialize`, `tools/list`, `tools/call`; `ping` is unsupported and reported as informational.
- `md_cs_mp_symbols_search` matches symbol names only. Use `md_cs_mp_code_search_text` for literal strings or comments.

### connapse (http)

Self-hosted knowledge base. Connapse 1.0.0, protocol 2025-06-18, 11 tools.

- The endpoint belongs in `servers.json`, not in this document.
- Auth: `Authorization: Bearer ${env:CONNAPSE_MCP_TOKEN}`. The gateway also accepts `X-API-Key`; `Api-Key`, `X-Auth-Token`, and bare-token `Authorization` are rejected with 401.
- Responses use SSE framing; the bridge parses `data:` frames.

Typical flow:

```
mcp call connapse container_list
mcp call connapse search_knowledge --args '{"query":"...","containerId":"<id>","mode":"hybrid","topK":5}'
mcp call connapse get_document --args '{"containerId":"<id>","fileId":"<docId>"}'
```

Notes:
- `containerId` is a GUID from `container_list`, not a name.
- Search takes roughly 3-6 s when it embeds the query; that is normal.
- Write tools (`upload_file`, `bulk_upload`, `delete_file`, `bulk_delete`, `container_create`, `container_delete`) mutate real data. Confirm before calling.

## Limitations

This is a bridge, not native tool registration. Every call spawns a process and completes a handshake (roughly 100-300 ms overhead, plus the server's own latency), there is no approval flow, and tool results arrive as command output. Native integration would require a host that publishes a tool catalog through `RuntimeHostCallbacks.ToolCallRequestedAsync` plus `ReplaceToolCatalogAsync`.
