import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
const executable = path.join(root, '.agent-tools/serena', process.platform === 'win32' ? 'Scripts/serena.exe' : 'bin/serena');
const args = process.argv.includes('--help') ? ['start-mcp-server', '--help'] : [
  'start-mcp-server', '--project', root, '--context', 'ide', '--transport', 'stdio',
  '--enable-web-dashboard', 'false', '--open-web-dashboard', 'false', '--enable-gui-log-window', 'false',
];
const child = spawn(executable, args, {
  cwd: root, stdio: 'inherit', windowsHide: true,
  env: { ...process.env, SERENA_HOME: path.join(root, '.agent-tools/serena-home') },
});
child.on('error', (error) => { console.error(`Serena unavailable. Run scripts/agents/setup.ps1. ${error.message}`); process.exitCode = 1; });
child.on('exit', (code) => { process.exitCode = code ?? 1; });
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
