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
 * Prefer `ASSETBLOCK_API_BASE_URL` when the public URL uses HTTPS with a dev cert Node cannot validate
 * (e.g. use `http://localhost:5088` here while the browser still uses `https://localhost:7000`).
 */
export function getServerApiBaseUrl(): string {
  const serverFirst = process.env.ASSETBLOCK_API_BASE_URL?.trim()
  const publicFallback = process.env.NEXT_PUBLIC_API_BASE_URL?.trim()
  const base = serverFirst || publicFallback
  if (!base) {
    throw new Error(
      'Set ASSETBLOCK_API_BASE_URL or NEXT_PUBLIC_API_BASE_URL for server-side API calls (see .env.example).',
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
