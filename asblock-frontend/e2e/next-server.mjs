import { readFile, rm, writeFile } from 'node:fs/promises'
import net from 'node:net'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { fileURLToPath, pathToFileURL } from 'node:url'

const frontendRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const e2eOutput = path.resolve(frontendRoot, '.next-e2e')
if (e2eOutput !== path.join(frontendRoot, '.next-e2e')) {
  throw new Error('E2E output directory resolved outside the frontend workspace.')
}

const nextCli = path.join(frontendRoot, 'node_modules', 'next', 'dist', 'bin', 'next')
const nextServerPath = fileURLToPath(import.meta.url)
const stubPath = path.join(frontendRoot, 'e2e', 'stub-api.mjs')
const schemaLoader = path.join(frontendRoot, 'e2e', 'schema-loader.mjs')

function wait(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

async function probePort(port, host) {
  await new Promise((resolve, reject) => {
    const probe = net.createServer()
    probe.once('error', reject)
    probe.listen(port, host, () => probe.close(resolve))
  })
}

async function ensurePortFree(port) {
  await probePort(port, '127.0.0.1')
  await probePort(port, undefined)
}

async function waitForUrl(url, child, timeout, name) {
  let childExited = false
  const childExit = new Promise((_, reject) => {
    child.once('exit', (code, signal) => {
      childExited = true
      reject(new Error(`${name} exited before becoming ready (code ${code}, signal ${signal}).`))
    })
  })
  const readiness = (async () => {
    const deadline = Date.now() + timeout
    while (Date.now() < deadline && !childExited) {
      try {
        const response = await fetch(url)
        if (response.ok) return
      } catch {}
      await wait(200)
    }
    if (childExited) return
    throw new Error(`${name} did not become ready at ${url} within ${timeout} ms.`)
  })()
  await Promise.race([readiness, childExit])
}

/** Closes the child's stdin so its own shutdown handlers run (and restore generated files), then terminates it if it does not exit. */
async function stopChild(child) {
  if (!child || child.exitCode !== null) return
  await new Promise((resolve) => {
    const forceTimer = setTimeout(() => child.kill('SIGKILL'), 15_000)
    const termTimer = setTimeout(() => child.kill('SIGTERM'), 5_000)
    child.once('exit', () => {
      clearTimeout(termTimer)
      clearTimeout(forceTimer)
      resolve()
    })
    child.stdin?.end()
  })
}

export default async function globalSetup() {
  const port = Number(process.env.PLAYWRIGHT_PORT ?? 3100)
  const apiPort = Number(process.env.PLAYWRIGHT_API_PORT ?? 3999)
  const baseURL = `http://localhost:${port}`
  const apiURL = `http://127.0.0.1:${apiPort}`
  const env = {
    ...process.env,
    PLAYWRIGHT_API_PORT: String(apiPort),
    NEXT_DIST_DIR: 'e2e',
    NEXT_PUBLIC_API_BASE_URL: apiURL,
    ASSETBLOCK_API_BASE_URL: apiURL,
    PORT: String(port),
  }

  const ports = [...new Set([apiPort, port])]
  for (const candidate of ports) await ensurePortFree(candidate)

  const stub = spawn(
    process.execPath,
    [
      '--experimental-strip-types',
      '--experimental-loader',
      pathToFileURL(schemaLoader).href,
      stubPath,
    ],
    { cwd: frontendRoot, env, stdio: ['pipe', 'inherit', 'inherit'], windowsHide: true },
  )
  let next
  try {
    await waitForUrl(`${apiURL}/health`, stub, 30_000, 'Stub API')
    next = spawn(process.execPath, [nextServerPath, '--serve'], {
      cwd: frontendRoot,
      env,
      stdio: ['pipe', 'inherit', 'inherit'],
      windowsHide: true,
    })
    await waitForUrl(baseURL, next, 180_000, 'Next server')
  } catch (error) {
    await Promise.all([stopChild(next), stopChild(stub)])
    throw error
  }

  return async () => {
    await Promise.all([stopChild(next), stopChild(stub)])
  }
}

async function runNextServer() {
  const port = Number(process.env.PLAYWRIGHT_PORT ?? 3100)
  const env = {
    ...process.env,
    NEXT_DIST_DIR: 'e2e',
    NEXT_PUBLIC_API_BASE_URL: `http://127.0.0.1:${process.env.PLAYWRIGHT_API_PORT ?? 3999}`,
    ASSETBLOCK_API_BASE_URL: `http://127.0.0.1:${process.env.PLAYWRIGHT_API_PORT ?? 3999}`,
    PORT: String(port),
  }

  let activeChild
  let stopping = false
  let stopPromise

  function stopActiveChild() {
    if (stopPromise) return stopPromise
    stopping = true
    process.stdin.pause()
    const child = activeChild
    if (!child || child.exitCode !== null) return Promise.resolve()

    stopPromise = new Promise((resolve) => {
      const timeout = setTimeout(() => child.kill('SIGKILL'), 10_000)
      child.once('exit', () => {
        clearTimeout(timeout)
        resolve()
      })
      if (!child.kill('SIGTERM')) {
        clearTimeout(timeout)
        resolve()
      }
    })
    return stopPromise
  }

  const requestShutdown = () => {
    void stopActiveChild()
  }
  process.stdin.resume()
  process.stdin.once('end', requestShutdown)
  process.stdin.once('close', requestShutdown)
  process.once('SIGINT', requestShutdown)
  process.once('SIGTERM', requestShutdown)

  await rm(e2eOutput, { recursive: true, force: true })
  const generatedTypeFiles = ['next-env.d.ts', 'tsconfig.json'].map((file) =>
    path.join(frontendRoot, file),
  )
  const originalTypeFiles = await Promise.all(generatedTypeFiles.map((file) => readFile(file)))

  let buildExit
  try {
    buildExit = await new Promise((resolve, reject) => {
      const build = spawn(process.execPath, [nextCli, 'build'], {
        cwd: frontendRoot,
        env,
        stdio: ['ignore', 'inherit', 'inherit'],
        windowsHide: true,
      })
      activeChild = build
      build.once('error', reject)
      build.once('exit', (code) => {
        activeChild = undefined
        resolve(code ?? 1)
      })
    })
  } finally {
    await Promise.all(
      generatedTypeFiles.map((file, index) => writeFile(file, originalTypeFiles[index])),
    )
  }

  if (stopping) return
  if (buildExit !== 0) {
    process.exitCode = buildExit
    return
  }

  const server = spawn(process.execPath, [nextCli, 'start', '--port', String(port)], {
    cwd: frontendRoot,
    env,
    stdio: ['ignore', 'inherit', 'inherit'],
    windowsHide: true,
  })
  activeChild = server
  server.once('error', (error) => {
    process.stderr.write(`${error.message}\n`)
    process.exitCode = 1
  })
  server.once('exit', (code) => {
    activeChild = undefined
    if (!stopping) process.exitCode = code ?? 1
  })
}

if (process.argv[2] === '--serve') await runNextServer()
