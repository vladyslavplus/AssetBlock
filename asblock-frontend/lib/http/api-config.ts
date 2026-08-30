/**
 * Public API base URL for browser-side fetch (must be NEXT_PUBLIC_*).
 * Configure in `.env.local` — see `.env.example`.
 */
export function getPublicApiBaseUrl(): string {
  const base = process.env.NEXT_PUBLIC_API_BASE_URL?.trim()
  if (!base) {
    throw new Error(
      'NEXT_PUBLIC_API_BASE_URL is not set. Copy .env.example to .env.local and set the AssetBlock API URL.',
    )
  }
  return base.replace(/\/+$/, '')
}

/**
 * Base URL for server-side BFF calls to AssetBlock Web API (Route Handlers, Server Actions).
 * Configured via ASSETBLOCK_API_BASE_URL (e.g. `http://localhost:5088` in local dev).
 */
export function getServerApiBaseUrl(): string {
  const base = process.env.ASSETBLOCK_API_BASE_URL?.trim()
  if (!base) {
    throw new Error(
      'ASSETBLOCK_API_BASE_URL is not set for server-side API calls. Copy .env.example to .env.local and set ASSETBLOCK_API_BASE_URL.',
    )
  }
  return base.replace(/\/+$/, '')
}

/**
 * Builds an absolute API URL for a path like `/api/assets` or `api/assets`.
 */
export function apiUrl(path: string): string {
  const base = getPublicApiBaseUrl()
  const p = path.startsWith('/') ? path : `/${path}`
  return `${base}${p}`
}
