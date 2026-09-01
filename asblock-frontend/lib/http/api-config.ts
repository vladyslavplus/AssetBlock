import { getPublicEnvironment, getServerEnvironment } from '@/lib/env'

/**
 * Public API base URL for browser-side fetch (must be NEXT_PUBLIC_*).
 * Configure in `.env.local` — see `.env.example`.
 */
export function getPublicApiBaseUrl(): string {
  return getPublicEnvironment().publicApiBaseUrl
}

/**
 * Base URL for server-side BFF calls to AssetBlock Web API (Route Handlers, Server Actions).
 * Configured via ASSETBLOCK_API_BASE_URL (e.g. `http://localhost:5088` in local dev).
 */
export function getServerApiBaseUrl(): string {
  return getServerEnvironment().serverApiBaseUrl
}

/**
 * Builds an absolute API URL for a path like `/api/assets` or `api/assets`.
 */
export function apiUrl(path: string): string {
  const base = getPublicApiBaseUrl()
  const p = path.startsWith('/') ? path : `/${path}`
  return `${base}${p}`
}
