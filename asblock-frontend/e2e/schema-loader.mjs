import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const frontendRoot = dirname(dirname(fileURLToPath(import.meta.url)))

export async function resolve(specifier, context, nextResolve) {
  if (specifier.startsWith('@/')) {
    const target = join(frontendRoot, `${specifier.slice(2)}.ts`)
    if (existsSync(target)) return nextResolve(pathToFileURL(target).href, context)
  }

  if (specifier.startsWith('.') && context.parentURL?.startsWith('file:')) {
    try {
      return await nextResolve(specifier, context)
    } catch (error) {
      if (error?.code !== 'ERR_MODULE_NOT_FOUND') throw error
      const target = new URL(`${specifier}.ts`, context.parentURL)
      if (existsSync(fileURLToPath(target))) return nextResolve(target.href, context)
      throw error
    }
  }

  return nextResolve(specifier, context)
}
