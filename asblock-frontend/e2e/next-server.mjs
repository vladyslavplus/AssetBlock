import { rm } from 'node:fs/promises'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const frontendRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const e2eOutput = path.resolve(frontendRoot, '.next-e2e')
if (e2eOutput !== path.join(frontendRoot, '.next-e2e')) {
  throw new Error('E2E output directory resolved outside the frontend workspace.')
}

const nextCli = path.join(frontendRoot, 'node_modules', 'next', 'dist', 'bin', 'next')
const port = Number(process.env.PLAYWRIGHT_PORT ?? 3100)
const env = {
  ...process.env,
  NEXT_DIST_DIR: 'e2e',
  NEXT_PUBLIC_API_BASE_URL: 'http://127.0.0.1:3999',
  ASSETBLOCK_API_BASE_URL: 'http://127.0.0.1:3999',
  PORT: String(port),
}

await rm(e2eOutput, { recursive: true, force: true })
const buildExit = await new Promise((resolve, reject) => {
  const build = spawn(process.execPath, [nextCli, 'build'], {
    cwd: frontendRoot,
    env,
    stdio: 'inherit',
    windowsHide: true,
  })
  build.once('error', reject)
  build.once('exit', (code) => resolve(code ?? 1))
})
if (buildExit !== 0) process.exit(buildExit)

const server = spawn(process.execPath, [nextCli, 'start', '--port', String(port)], {
  cwd: frontendRoot,
  env,
  stdio: 'inherit',
  windowsHide: true,
})
const stopServer = () => {
  if (!server.killed) server.kill()
}
process.once('SIGINT', stopServer)
process.once('SIGTERM', stopServer)
server.once('error', (error) => {
  process.stderr.write(`${error.message}\n`)
  process.exitCode = 1
})
server.once('exit', (code) => {
  process.exitCode = code ?? 1
})
