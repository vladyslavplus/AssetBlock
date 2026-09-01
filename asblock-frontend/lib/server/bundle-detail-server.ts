import { cache } from 'react'
import { bundleDetailResponseSchema } from '@/lib/bundles/bundle-schemas'
import type { BundleDetail } from '@/lib/bundles/bundle-types'
import { fetchBackendPublic } from '@/lib/server/fetch-backend'

const UUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i

async function readJson<T>(res: Response): Promise<T | undefined> {
  const text = await res.text()
  if (!text) return undefined
  try {
    return JSON.parse(text) as T
  } catch {
    return undefined
  }
}

export type BundleServerResult =
  | { status: 'success'; bundle: BundleDetail }
  | { status: 'not_found' }
  | { status: 'unavailable'; error?: string }

/**
 * Server-side loader for public bundle details with request-scoped caching.
 * - 'not_found': confirmed 404 or invalid UUID
 * - 'unavailable': transient 5xx, timeout, or upstream network failure (falls back to client retry without crashing SSR)
 * - 'success': validated BundleDetail
 */
export const getBundleDetailCached = cache(async (id: string): Promise<BundleServerResult> => {
  if (!UUID_REGEX.test(id.trim())) {
    return { status: 'not_found' }
  }

  try {
    const res = await fetchBackendPublic(`/api/bundles/${encodeURIComponent(id)}`)
    if (res.status === 404) {
      return { status: 'not_found' }
    }
    if (!res.ok) {
      return { status: 'unavailable', error: `Status ${res.status}` }
    }

    const json = await readJson<unknown>(res)
    if (!json) {
      return { status: 'unavailable', error: 'Malformed JSON' }
    }

    const parsed = bundleDetailResponseSchema.safeParse(json)
    if (!parsed.success) {
      return { status: 'unavailable', error: 'Invalid schema' }
    }

    return { status: 'success', bundle: parsed.data }
  } catch (err) {
    return {
      status: 'unavailable',
      error: err instanceof Error ? err.message : 'Network error',
    }
  }
})
