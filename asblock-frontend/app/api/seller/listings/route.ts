import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

/**
 * Proxies GET /api/users/me/assets (seller's listed assets) with session cookies.
 */
export async function GET(request: Request) {
  const store = await cookies()
  const url = new URL(request.url)
  const qs = url.searchParams.toString()
  const backendPath = `/api/users/me/assets${qs ? `?${qs}` : ''}`
  const res = await fetchBackendAuthorized(store, backendPath, { method: 'GET' })
  return forwardBackendResponse(res)
}
