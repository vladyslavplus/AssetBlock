import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const launcher = path.join(root, 'scripts/agents/start-serena.mjs');
const tools = ['initial_instructions', 'get_symbols_overview', 'find_symbol', 'find_referencing_symbols', 'search_for_pattern', 'get_current_config'];
const quote = (value) => JSON.stringify(value.replaceAll('\\', '/'));
const codexPath = path.join(root, '.codex/config.toml');
fs.mkdirSync(path.dirname(codexPath), { recursive: true });
const existing = fs.existsSync(codexPath) ? fs.readFileSync(codexPath, 'utf8') : '';
if (/^\s*\[mcp_servers\.assetblock_serena\]/m.test(existing)) {
  console.log('Codex Serena entry exists; preserved.');
} else {
  fs.appendFileSync(codexPath, `${existing && !existing.endsWith('\n') ? '\n' : ''}
[mcp_servers.assetblock_serena]
command = ${quote(process.execPath)}
args = [${quote(launcher)}]
cwd = ${quote(root)}
startup_timeout_sec = 180
tool_timeout_sec = 120
enabled_tools = [${tools.map(quote).join(', ')}]
`);
}
const cursorPath = path.join(root, '.cursor/mcp.json');
fs.mkdirSync(path.dirname(cursorPath), { recursive: true });
const cursor = fs.existsSync(cursorPath) ? JSON.parse(fs.readFileSync(cursorPath, 'utf8')) : {};
cursor.mcpServers ??= {};
if (cursor.mcpServers.assetblock_serena) console.log('Cursor Serena entry exists; preserved.');
else {
  cursor.mcpServers.assetblock_serena = { command: process.execPath, args: [launcher] };
  fs.writeFileSync(cursorPath, `${JSON.stringify(cursor, null, 2)}\n`);
}
console.log('Project client configuration ready. Reload MCP in a trusted workspace.');
