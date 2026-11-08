#!/usr/bin/env node
// mcp-bridge.mjs - zero-dependency MCP client bridge (stdio + streamable HTTP)
// Only node built-ins are used: child_process / fs / url / path.

import { spawn } from 'node:child_process';
import { readFileSync, writeFileSync, existsSync, mkdirSync, readdirSync, rmSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const CONFIG_PATH = path.join(ROOT, 'servers.json');
const CATALOG_DIR = path.join(ROOT, 'catalog');
const CATALOG_INDEX = path.join(CATALOG_DIR, 'index.json');
const CATALOG_INDEX_MD = path.join(CATALOG_DIR, 'index.md');
const CATALOG_SERVERS = path.join(CATALOG_DIR, 'servers');
const PROTOCOL_VERSION = '2025-06-18';
const DEFAULT_TIMEOUT = 60000;
const BRIDGE_VERSION = '1.0.0';

const out = (s) => process.stdout.write(s + '\n');
const err = (s) => process.stderr.write(s + '\n');
function die(msg, code = 1) { err('Error: ' + msg); process.exit(code); }

function expandEnv(value, missing) {
  if (typeof value === 'string') {
    return value.replace(/\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}/g, (_m, name) => {
      const v = process.env[name];
      if (v === undefined || v === '') { if (!missing.includes(name)) missing.push(name); return ''; }
      return v;
    });
  }
  if (Array.isArray(value)) return value.map((v) => expandEnv(v, missing));
  if (value && typeof value === 'object') {
    const o = {};
    for (const [k, v] of Object.entries(value)) o[k] = expandEnv(v, missing);
    return o;
  }
  return value;
}

function resolveExecutable(cmd) {
  if (!cmd) return cmd;
  if (process.platform !== 'win32') return cmd;
  if (path.isAbsolute(cmd) || cmd.includes('\\') || cmd.includes('/')) return cmd;
  const exts = ['.exe', '.cmd', '.bat', '.com', ''];
  const dirs = (process.env.PATH || '').split(';').filter(Boolean);
  for (const d of dirs) {
    for (const e of exts) {
      const full = path.join(d, cmd + e);
      if (existsSync(full)) return full;
    }
  }
  return cmd;
}

function loadConfig() {
  if (!existsSync(CONFIG_PATH)) die('config not found: ' + CONFIG_PATH, 2);
  let raw;
  try { raw = JSON.parse(readFileSync(CONFIG_PATH, 'utf8')); }
  catch (e) { die('config parse error: ' + e.message, 2); }
  const table = raw.mcpServers || raw.servers || {};
  const servers = {};
  for (const [name, value] of Object.entries(table)) {
    const missing = [];
    const def = expandEnv(value, missing);
    const type = String(def.transport || def.type || (def.url ? 'http' : 'stdio')).toLowerCase();
    let transport;
    if (type === 'stdio' || type === 'command' || type === '\u547d\u4ee4' || type === 'process') transport = 'stdio';
    else if (type === 'http' || type === 'sse' || type === 'streamable-http' || type === 'url') transport = 'http';
    else die('server "' + name + '" has unsupported transport: ' + type, 2);
    const d = {
      name,
      transport,
      command: resolveExecutable(def.command),
      args: Array.isArray(def.args) ? def.args.map(String) : [],
      env: def.env || {},
      cwd: def.cwd || ROOT,
      url: def.url,
      headers: def.headers || {},
      timeoutMs: Number(def.timeoutMs || DEFAULT_TIMEOUT),
      missingEnv: missing,
      disabled: def.disabled === true,
      description: def.description || ''
    };
    if (transport === 'stdio' && !d.command) die('server "' + name + '" is missing command', 2);
    if (transport === 'http' && !d.url) die('server "' + name + '" is missing url', 2);
    servers[name] = d;
  }
  return servers;
}

function quoteWin(a) {
  const s = String(a);
  return /[\s"]/.test(s) ? '"' + s.replace(/"/g, '\\"') + '"' : s;
}

class StdioClient {
  constructor(def) {
    this.def = def;
    this.pending = new Map();
    this.nextId = 1;
    this.buf = '';
    this.stderr = '';
    this.closed = false;
    const ext = path.extname(def.command).toLowerCase();
    const useShell = ext === '.cmd' || ext === '.bat';
    const args = def.args;
    this.child = useShell
      ? spawn(process.env.ComSpec || 'cmd.exe',
          ['/d', '/s', '/c', '"' + def.command + '" ' + args.map(quoteWin).join(' ')],
          { cwd: def.cwd, env: { ...process.env, ...def.env }, windowsHide: true })
      : spawn(def.command, args,
          { cwd: def.cwd, env: { ...process.env, ...def.env }, windowsHide: true });
    this.child.stdout.setEncoding('utf8');
    this.child.stderr.setEncoding('utf8');
    this.child.stdout.on('data', (d) => this.onData(d));
    this.child.stderr.on('data', (d) => {
      this.stderr += d;
      if (this.stderr.length > 131072) this.stderr = this.stderr.slice(-65536);
    });
    this.child.on('error', (e) => this.rejectAll('spawn failed: ' + e.message));
    this.child.on('exit', (code, sig) => {
      this.closed = true;
      const extra = this.stderr.trim() ? ' | stderr: ' + this.stderr.trim().slice(-1200) : '';
      this.rejectAll('server exited code=' + code + (sig ? ' signal=' + sig : '') + extra);
    });
  }
  rejectAll(reason) {
    for (const [, p] of this.pending) { clearTimeout(p.timer); p.reject(new Error(reason)); }
    this.pending.clear();
  }
  onData(chunk) {
    this.buf += chunk;
    for (;;) {
      if (!this.buf) return;
      if (/^content-length:/i.test(this.buf)) {
        const end = this.buf.indexOf('\r\n\r\n');
        if (end < 0) return;
        const m = /content-length:\s*(\d+)/i.exec(this.buf.slice(0, end));
        if (!m) { this.buf = this.buf.slice(end + 4); continue; }
        const len = Number(m[1]);
        const start = end + 4;
        if (this.buf.length < start + len) return;
        const body = this.buf.slice(start, start + len);
        this.buf = this.buf.slice(start + len);
        this.handleLine(body);
      } else {
        const nl = this.buf.indexOf('\n');
        if (nl < 0) return;
        const line = this.buf.slice(0, nl).trim();
        this.buf = this.buf.slice(nl + 1);
        if (line) this.handleLine(line);
      }
    }
  }
  handleLine(line) {
    let msg;
    try { msg = JSON.parse(line); } catch { this.stderr += '\n[non-json stdout] ' + line.slice(0, 500); return; }
    this.dispatch(msg);
  }
  dispatch(msg) {
    if (msg && msg.id !== undefined && msg.id !== null && (msg.result !== undefined || msg.error !== undefined)) {
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      clearTimeout(p.timer);
      if (msg.error) p.reject(new Error(p.method + ' failed: ' + (msg.error.message || JSON.stringify(msg.error))));
      else p.resolve(msg.result);
    }
  }
  write(payload) {
    return new Promise((resolve, reject) => {
      if (this.closed) return reject(new Error('server process already exited'));
      this.child.stdin.write(payload + '\n', 'utf8', (e) => (e ? reject(e) : resolve()));
    });
  }
  request(method, params, timeoutMs) {
    const id = this.nextId++;
    const payload = JSON.stringify({ jsonrpc: '2.0', id, method, params: params === undefined ? {} : params });
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error('timeout after ' + timeoutMs + 'ms: ' + method));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer, method });
      this.write(payload).catch((e) => { clearTimeout(timer); this.pending.delete(id); reject(e); });
    });
  }
  notify(method, params) {
    return this.write(JSON.stringify({ jsonrpc: '2.0', method, params: params === undefined ? {} : params }));
  }
  async close() {
    if (this.closed) return;
    try { this.child.stdin.end(); } catch {}
    await new Promise((r) => {
      const t = setTimeout(() => { try { this.child.kill(); } catch {} r(); }, 1500);
      this.child.once('exit', () => { clearTimeout(t); r(); });
    });
  }
}class HttpClient {
  constructor(def) {
    this.def = def;
    this.nextId = 1;
    this.sessionId = null;
    this.protocolVersion = null;
  }
  headers(extra) {
    const h = {
      'content-type': 'application/json',
      'accept': 'application/json, text/event-stream',
      ...this.def.headers,
      ...(extra || {})
    };
    if (this.sessionId) h['mcp-session-id'] = this.sessionId;
    if (this.protocolVersion) h['mcp-protocol-version'] = this.protocolVersion;
    return h;
  }
  async post(payload, timeoutMs) {
    const ac = new AbortController();
    const timer = setTimeout(() => ac.abort(), timeoutMs);
    let res;
    try {
      res = await fetch(this.def.url, {
        method: 'POST', headers: this.headers(), body: JSON.stringify(payload), signal: ac.signal
      });
    } catch (e) {
      clearTimeout(timer);
      throw new Error(e.name === 'AbortError' ? 'timeout after ' + timeoutMs + 'ms' : 'http error: ' + e.message);
    }
    const sid = res.headers.get('mcp-session-id');
    if (sid) this.sessionId = sid;
    const ctype = (res.headers.get('content-type') || '').toLowerCase();
    const text = await res.text();
    clearTimeout(timer);
    if (!res.ok) throw new Error('http ' + res.status + ': ' + text.slice(0, 800));
    if (ctype.includes('text/event-stream')) {
      return { messages: parseSse(text), sessionId: sid };
    }
    if (!text.trim()) return { messages: [], sessionId: sid };
    let parsed;
    try { parsed = JSON.parse(text); }
    catch { throw new Error('invalid json response: ' + text.slice(0, 500)); }
    if (Array.isArray(parsed)) return { messages: parsed, sessionId: sid };
    return { messages: [parsed], sessionId: sid };
  }
  async request(method, params, timeoutMs) {
    const id = this.nextId++;
    const r = await this.post({ jsonrpc: '2.0', id, method, params: params === undefined ? {} : params }, timeoutMs);
    const hit = r.messages.find((m) => m && m.id === id && (m.result !== undefined || m.error !== undefined));
    if (!hit) throw new Error('no response payload for ' + method);
    if (hit.error) throw new Error(method + ' failed: ' + (hit.error.message || JSON.stringify(hit.error)));
    return hit.result;
  }
  async notify(method, params) {
    await this.post({ jsonrpc: '2.0', method, params: params === undefined ? {} : params }, this.def.timeoutMs);
  }
  async close() {
    if (!this.sessionId) return;
    try {
      await fetch(this.def.url, { method: 'DELETE', headers: this.headers() });
    } catch {}
  }
}

function parseSse(text) {
  const messages = [];
  for (const block of text.split(/\r?\n\r?\n/)) {
    const dataLines = [];
    for (const line of block.split(/\r?\n/)) {
      if (line.startsWith('data:')) dataLines.push(line.slice(5).trimStart());
    }
    if (!dataLines.length) continue;
    const joined = dataLines.join('\n').trim();
    if (!joined) continue;
    try { messages.push(JSON.parse(joined)); } catch {}
  }
  return messages;
}

async function openClient(def) {
  const client = def.transport === 'stdio' ? new StdioClient(def) : new HttpClient(def);
  const init = await client.request('initialize', {
    protocolVersion: PROTOCOL_VERSION,
    capabilities: { roots: { listChanged: false }, elicitation: {} },
    clientInfo: { name: 'mcp-bridge', version: BRIDGE_VERSION }
  }, def.timeoutMs);
  if (client instanceof HttpClient) {
    client.protocolVersion = (init && init.protocolVersion) || PROTOCOL_VERSION;
  }
  try { await client.notify('notifications/initialized', {}); } catch {}
  return { client, init };
}

async function withClient(def, fn) {
  const { client, init } = await openClient(def);
  try { return await fn(client, init); }
  finally { try { await client.close(); } catch {} }
}

function pickServer(servers, name) {
  if (!name) die('server name is required; run "mcp list" to see configured servers', 2);
  const s = servers[name];
  if (!s) die('unknown server "' + name + '". configured: ' + Object.keys(servers).join(', '), 2);
  return s;
}

function warnMissing(s) {
  if (s.missingEnv && s.missingEnv.length) {
    err('Warning: server "' + s.name + '" references unset env: ' + s.missingEnv.join(', '));
  }
}

function flattenTools(serverName, result) {
  const list = (result && Array.isArray(result.tools)) ? result.tools : [];
  return list.map((t) => ({
    server: serverName,
    name: t.name,
    title: t.title || t.annotations && t.annotations.title || '',
    description: (t.description || '').replace(/\s+/g, ' ').trim(),
    inputSchema: t.inputSchema || { type: 'object', properties: {} }
  }));
}function parseArgv(argv) {
  const flags = {};
  const positionals = [];
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--json') { flags.json = true; continue; }
    if (a === '-h' || a === '--help' || a === '-?') { flags.help = true; continue; }
    if (a === '--version') { flags.version = true; continue; }
    if (a === '--quiet') { flags.quiet = true; continue; }
    if (a === '--no-probe') { flags['no-probe'] = true; continue; }
    if (a.startsWith('--') || a === '-Args' || a === '-args') {
      const bare = a.replace(/^--?/, '').toLowerCase();
      const key = bare === 'args' || bare === 'args-file' ? (bare === 'args' ? 'args' : 'args-file') : bare;
      const val = argv[i + 1];
      if (val === undefined) die('option ' + a + ' requires a value', 2);
      if (key === 'arg') { flags.arg = flags.arg || []; flags.arg.push(val); }
      else flags[key] = val;
      i++; continue;
    }
    positionals.push(a);
  }
  return { flags, positionals };
}

function coerceScalar(v) {
  const s = String(v);
  const t = s.trim();
  if (t === 'true') return true;
  if (t === 'false') return false;
  if (t === 'null') return null;
  if (/^-?\d+$/.test(t) && t.length < 16) return Number(t);
  if ((t.startsWith('{') && t.endsWith('}')) || (t.startsWith('[') && t.endsWith(']'))) {
    try { return JSON.parse(t); } catch {}
  }
  return s;
}

function collectLooseArgs(flags) {
  const pairs = flags.arg;
  if (!pairs) return null;
  const list = Array.isArray(pairs) ? pairs : [pairs];
  const o = {};
  for (const pair of list) {
    const idx = String(pair).indexOf('=');
    if (idx <= 0) die('--arg expects key=value, got: ' + pair, 2);
    const k = String(pair).slice(0, idx);
    const v = String(pair).slice(idx + 1);
    o[k] = coerceScalar(v);
  }
  return o;
}

function resolveCallArgs(flags) {
  const loose = collectLooseArgs(flags);
  let raw = flags.args;
  if (flags['args-file']) {
    const p = path.resolve(String(flags['args-file']));
    if (!existsSync(p)) die('args file not found: ' + p, 2);
    raw = readFileSync(p, 'utf8');
  }
  if (raw === undefined || raw === '') return loose || {};
  if (raw === '@stdin') raw = readFileSync(0, 'utf8');
  let parsed;
  try { parsed = JSON.parse(raw); }
  catch (e) { die('arguments must be valid JSON: ' + e.message, 2); }
  return loose ? { ...parsed, ...loose } : parsed;
}

function typeLabel(schema, isRequired) {
  if (!schema) return 'any';
  let nullable = false;
  const base = (s) => {
    if (Array.isArray(s.type)) {
      const real = s.type.filter((t) => t !== 'null');
      if (s.type.includes('null')) nullable = true;
      return real.length ? real.join('|') : 'any';
    }
    if (s.type) return s.type;
    if (Array.isArray(s.anyOf)) return [...new Set(s.anyOf.map(base))].join('|');
    if (Array.isArray(s.oneOf)) return [...new Set(s.oneOf.map(base))].join('|');
    return 'any';
  };
  const t = base(schema);
  return isRequired && nullable ? t + '|null' : t;
}
function printToolResult(result) {
  if (!result) { out('(empty result)'); return; }
  if (Array.isArray(result.content)) {
    for (const item of result.content) {
      if (item && item.type === 'text' && typeof item.text === 'string') out(item.text);
      else if (item && item.type === 'image') out('[image ' + (item.mimeType || '') + ' ' + String(item.data || '').length + ' base64 chars]');
      else if (item && item.type === 'resource') out('[resource ' + ((item.resource && item.resource.uri) || '') + ']');
      else out(JSON.stringify(item, null, 2));
    }
  } else {
    out(JSON.stringify(result, null, 2));
  }
  if (result.isError) { err('(tool reported isError=true)'); process.exitCode = 1; }
  if (result.structuredContent) {
    out('');
    out('structuredContent:');
    out(JSON.stringify(result.structuredContent, null, 2));
  }
}async function cmdList(servers, flags) {
  const rows = [];
  for (const def of Object.values(servers)) {
    warnMissing(def);
    const row = {
      name: def.name, transport: def.transport, disabled: def.disabled,
      target: def.transport === 'stdio' ? [def.command, ...def.args].join(' ') : def.url,
      status: def.disabled ? 'disabled' : 'unchecked', protocol: null,
      serverName: null, serverVersion: null, tools: null, error: null, description: def.description
    };
    if (!def.disabled && !flags['no-probe']) {
      try {
        await withClient(def, async (client, init) => {
          row.status = 'ok';
          row.protocol = (init && init.protocolVersion) || null;
          row.serverName = (init && init.serverInfo && init.serverInfo.name) || null;
          row.serverVersion = (init && init.serverInfo && init.serverInfo.version) || null;
          const tl = await client.request('tools/list', {}, def.timeoutMs);
          row.tools = flattenTools(def.name, tl).length;
        });
      } catch (e) {
        row.status = 'error';
        row.error = e.message;
      }
    }
    rows.push(row);
  }
  if (flags.json) { out(JSON.stringify({ version: BRIDGE_VERSION, config: CONFIG_PATH, servers: rows }, null, 2)); return; }
  out('Config: ' + CONFIG_PATH);
  if (!rows.length) { out('(no servers configured)'); return; }
  out('');
  for (const r of rows) {
    const mark = r.status === 'ok' ? '[ok]  ' : r.status === 'error' ? '[FAIL]' : '[' + r.status + ']';
    out(mark + ' ' + r.name + '  (' + r.transport + ')');
    out('       target : ' + r.target);
    if (r.description) out('       about  : ' + r.description);
    if (r.status === 'ok') out('       server : ' + r.serverName + ' ' + (r.serverVersion || '') + ' | protocol ' + r.protocol + ' | ' + r.tools + ' tool(s)');
    if (r.status === 'error') out('       error  : ' + r.error);
  }
}

async function cmdTools(servers, name, flags) {
  const def = pickServer(servers, name);
  warnMissing(def);
  const tools = await withClient(def, async (client) => {
    const r = await client.request('tools/list', {}, def.timeoutMs);
    return flattenTools(def.name, r);
  });
  if (flags.json) { out(JSON.stringify({ server: def.name, count: tools.length, tools }, null, 2)); return; }
  out('Server: ' + def.name + ' | ' + tools.length + ' tool(s)');
  out('');
  for (const t of tools) {
    out('* ' + t.name + (t.title ? '  (' + t.title + ')' : ''));
    if (t.description) out('    ' + t.description);
    const props = Object.entries((t.inputSchema && t.inputSchema.properties) || {});
    if (props.length) {
      const req = new Set((t.inputSchema && t.inputSchema.required) || []);
      out('    args: ' + props.map(([k, v]) => k + (req.has(k) ? '' : '?') + ':' + typeLabel(v, req.has(k))).join(', '));
    }
  }
}

async function cmdCall(servers, name, tool, flags) {
  const def = pickServer(servers, name);
  if (!tool) die('tool name is required; run "mcp tools ' + name + '"', 2);
  warnMissing(def);
  const args = resolveCallArgs(flags);
  const result = await withClient(def, async (client) =>
    client.request('tools/call', { name: tool, arguments: args }, def.timeoutMs));
  if (flags.json) { out(JSON.stringify(result, null, 2)); return; }
  printToolResult(result);
}async function cmdIndex(servers, flags) {
  mkdirSync(CATALOG_SERVERS, { recursive: true });
  const index = { generatedAt: new Date().toISOString(), bridgeVersion: BRIDGE_VERSION, configPath: CONFIG_PATH, servers: {} };
  let totalTools = 0;
  for (const def of Object.values(servers)) {
    warnMissing(def);
    const entry = {
      name: def.name, transport: def.transport,
      target: def.transport === 'stdio' ? [def.command, ...def.args].join(' ') : def.url,
      description: def.description, disabled: def.disabled, status: 'error',
      protocolVersion: null, serverInfo: null, toolCount: 0, error: null,
      shard: path.join('servers', def.name + '.json')
    };
    if (!def.disabled) {
      try {
        await withClient(def, async (client, init) => {
          entry.status = 'ok';
          entry.protocolVersion = (init && init.protocolVersion) || null;
          entry.serverInfo = (init && init.serverInfo) || null;
          const tl = await client.request('tools/list', {}, def.timeoutMs);
          const tools = flattenTools(def.name, tl);
          entry.toolCount = tools.length;
          totalTools += tools.length;
          writeFileSync(path.join(CATALOG_DIR, entry.shard), JSON.stringify({
            server: def.name, generatedAt: index.generatedAt, toolCount: tools.length, tools
          }, null, 2), 'utf8');
        });
      } catch (e) { entry.error = e.message; entry.shard = null; }
    } else { entry.status = 'disabled'; entry.shard = null; }
    index.servers[def.name] = entry;
    if (!flags.quiet) err('[' + entry.status + '] ' + def.name + ' -> ' + entry.toolCount + ' tool(s)' + (entry.error ? ' | ' + entry.error : ''));
  }
  writeFileSync(CATALOG_INDEX, JSON.stringify(index, null, 2), 'utf8');
  writeFileSync(CATALOG_INDEX_MD, renderIndexMarkdown(index), 'utf8');
  if (flags.json) { out(JSON.stringify({ index: CATALOG_INDEX, markdown: CATALOG_INDEX_MD, shards: Object.values(index.servers).filter((s) => s.shard).length, tools: totalTools }, null, 2)); return; }
  out('Wrote ' + CATALOG_INDEX + '  (compact index)');
  out('Wrote ' + CATALOG_INDEX_MD);
  out('Wrote ' + CATALOG_SERVERS + '\\<server>.json  (' + Object.values(index.servers).filter((s) => s.shard).length + ' shard(s))');
  out('Servers: ' + Object.keys(index.servers).length + ' | Tools: ' + totalTools);
}

function readShard(serverName) {
  const p = path.join(CATALOG_SERVERS, serverName + '.json');
  if (!existsSync(p)) die('no catalog shard for "' + serverName + '"; run "mcp index" first', 2);
  try { return JSON.parse(readFileSync(p, 'utf8')); }
  catch (e) { die('shard parse error (' + p + '): ' + e.message, 2); }
}

function findToolInShards(toolName) {
  if (!existsSync(CATALOG_SERVERS)) return null;
  for (const f of readdirSync(CATALOG_SERVERS).filter((n) => n.endsWith('.json'))) {
    try {
      const shard = JSON.parse(readFileSync(path.join(CATALOG_SERVERS, f), 'utf8'));
      const hit = (shard.tools || []).find((t) => t.name === toolName);
      if (hit) return { server: shard.server, tool: hit };
    } catch {}
  }
  return null;
}

function renderIndexMarkdown(index) {
  const lines = [];
  lines.push('# MCP Catalog Index');
  lines.push('');
  lines.push('Generated: ' + index.generatedAt + ' | bridge ' + index.bridgeVersion);
  lines.push('Config: `' + index.configPath + '`');
  lines.push('');
  lines.push('Per-server shards live in `servers/<server>.json` (full input schemas).');
  lines.push('Load a shard only when you need a tool signature: `mcp schema <server> [tool]`.');
  lines.push('');
  for (const [name, s] of Object.entries(index.servers)) {
    lines.push('## ' + name + ' (' + s.status + ', ' + s.toolCount + ' tools)');
    lines.push('');
    lines.push('- transport: ' + s.transport);
    lines.push('- target: `' + s.target + '`');
    if (s.serverInfo) lines.push('- server: ' + s.serverInfo.name + ' ' + (s.serverInfo.version || '') + ' | protocol ' + (s.protocolVersion || ''));
    if (s.description) lines.push('- about: ' + s.description);
    if (s.shard) lines.push('- shard: `' + String(s.shard).replace(/\\/g, '/') + '`');
    if (s.error) lines.push('- error: ' + s.error);
    if (s.shard && existsSync(path.join(CATALOG_DIR, s.shard))) {
      const shard = readShard(name);
      lines.push('');
      lines.push('Tools: ' + (shard.tools || []).map((t) => '`' + t.name + '`').join(', '));
    }
    lines.push('');
  }
  return lines.join('\n');
}
function cmdSchema(flags, positionals) {
  if (flags.tool) {
    const found = findToolInShards(flags.tool);
    if (!found) die('tool "' + flags.tool + '" not found in any catalog shard; run "mcp index"', 2);
    err('(resolved to server "' + found.server + '")');
    out(JSON.stringify(found.tool, null, 2));
    return;
  }
  const server = positionals[0];
  const toolName = positionals[1];
  if (!server) die('usage: mcp schema <server> [tool]  OR  mcp schema --tool <toolName>', 2);
  const shard = readShard(server);
  if (!toolName) {
    if (flags.json) { out(JSON.stringify(shard, null, 2)); return; }
    out('Server: ' + server + ' | ' + (shard.tools || []).length + ' tool(s) | shard ' + path.join(CATALOG_SERVERS, server + '.json'));
    out('');
    for (const t of shard.tools || []) {
      out('* ' + t.name);
      if (t.description) out('    ' + t.description);
    }
    return;
  }
  const t = (shard.tools || []).find((x) => x.name === toolName);
  if (!t) die('tool "' + toolName + '" not found on server "' + server + '"', 2);
  out(JSON.stringify(t, null, 2));
}

async function cmdDoctor(servers, only, flags) {
  const names = only ? [only] : Object.keys(servers);
  const report = [];
  for (const n of names) {
    const def = pickServer(servers, n);
    const r = { name: n, ok: false, steps: [] };
    try {
      if (def.missingEnv && def.missingEnv.length) { r.steps.push({ step: 'env', ok: false, detail: 'unset: ' + def.missingEnv.join(', ') }); throw new Error('missing env: ' + def.missingEnv.join(', ')); }
      r.steps.push({ step: 'env', ok: true, detail: 'ok' });
      if (def.transport === 'stdio') {
        const exists = path.isAbsolute(def.command) ? existsSync(def.command) : true;
        r.steps.push({ step: 'executable', ok: exists, detail: def.command });
        if (!exists) throw new Error('executable not found: ' + def.command);
      } else {
        r.steps.push({ step: 'endpoint', ok: true, detail: def.url });
      }
      await withClient(def, async (client, init) => {
        r.steps.push({ step: 'initialize', ok: true, detail: 'protocol ' + (init && init.protocolVersion) + ' | ' + JSON.stringify(init && init.serverInfo) });
        const tl = await client.request('tools/list', {}, def.timeoutMs);
        r.steps.push({ step: 'tools/list', ok: true, detail: flattenTools(n, tl).length + ' tool(s)' });
        try {
          await client.request('ping', {}, def.timeoutMs);
          r.steps.push({ step: 'ping', ok: true, detail: 'ok' });
        } catch (e) { r.steps.push({ step: 'ping', ok: false, detail: 'optional: ' + e.message }); }
        r.ok = true;
      });
    } catch (e) { r.steps.push({ step: 'fatal', ok: false, detail: e.message }); }
    report.push(r);
  }
  if (flags.json) { out(JSON.stringify(report, null, 2)); return; }
  for (const r of report) {
    out((r.ok ? '[ok]  ' : '[FAIL]') + ' ' + r.name);
    for (const s of r.steps) out('       ' + (s.ok ? '-' : '!') + ' ' + s.step + ': ' + s.detail);
  }
  if (!report.every((r) => r.ok)) process.exitCode = 1;
}

function printHelp() {
  out('mcp-bridge ' + BRIDGE_VERSION + ' - zero-dependency MCP client bridge');
  out('');
  out('Usage: mcp <command> [options]');
  out('');
  out('Commands:');
  out('  list                      List configured servers with connectivity');
  out('  tools <server>            Show tools exposed by a server');
  out('  call <server> <tool>      Invoke a tool');
  out('  index                     Rebuild catalog index and per-server shards');
  out('  schema <server> [tool]    Read tool schema from a shard (no server contact)');
  out('  doctor [server]           Probe transport, handshake, and tool listing');
  out('  show <server>             Print the resolved server definition');
  out('');
  out('Options:');
  out('  --args <json>             JSON object passed to tool arguments');
  out('  --args-file <path>        Read tool arguments JSON from a file');
  out('  --json                    Machine-readable output');
  out('  --quiet                   Suppress progress on stderr');
  out('  --no-probe                Skip connectivity probing (list only)');
  out('');
  out('Config: ' + CONFIG_PATH);
  out('Environment placeholders: ${env:NAME} inside servers.json');
}

async function main() {
  const argv = process.argv.slice(2);
  if (!argv.length) { printHelp(); return; }
  const { flags, positionals } = parseArgv(argv);
  if (flags.version) { out(BRIDGE_VERSION); return; }
  const cmd = (positionals.shift() || '').toLowerCase();
  if (flags.help || cmd === 'help') { printHelp(); return; }
  const servers = loadConfig();
  switch (cmd) {
    case 'list': case 'ls': await cmdList(servers, flags); break;
    case 'tools': await cmdTools(servers, positionals[0], flags); break;
    case 'call': case 'invoke': await cmdCall(servers, positionals[0], positionals[1], flags); break;
    case 'index': await cmdIndex(servers, flags); break;
    case 'schema': case 'cat': cmdSchema(flags, positionals); break;
    case 'doctor': await cmdDoctor(servers, positionals[0], flags); break;
    case 'show': { const s = pickServer(servers, positionals[0]); out(JSON.stringify(s, null, 2)); break; }
    default: die('unknown command "' + cmd + '"; run "mcp --help"', 2);
  }
}

main().catch((e) => { err('Error: ' + ((e && e.message) || String(e))); process.exit(1); });