import { cookies } from 'next/headers'

import { analyticsCollectionsBackendQuery } from '@/lib/server/analytics-bff-params'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

/** Proxies GET /api/seller/analytics/collections with session cookies. */
export async function GET(request: Request) {
  const query = analyticsCollectionsBackendQuery(new URL(request.url))
  if (!query.ok) return query.response

  const store = await cookies()
  const backendPath = `/api/seller/analytics/collections${query.qs}`
  const res = await fetchBackendAuthorized(store, backendPath, { method: 'GET' })
  return forwardBackendResponse(res)
}
