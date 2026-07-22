import path from 'node:path'
import { fileURLToPath } from 'node:url'

/** Frontend app root — parent monorepo also has a pnpm-lock.yaml (husky), which confuses Turbopack root inference. */
const appRoot = path.dirname(fileURLToPath(import.meta.url))

/** @type {import('next').NextConfig} */
const nextConfig = {
  reactCompiler: true,
  typescript: {
    ignoreBuildErrors: false,
  },
  turbopack: {
    root: appRoot,
  },
  outputFileTracingRoot: appRoot,
}

export default nextConfig
