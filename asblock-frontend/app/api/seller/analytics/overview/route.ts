import { cookies } from 'next/headers'

import { analyticsOverviewBackendQuery } from '@/lib/server/analytics-bff-params'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardAuthenticatedBackendResponse } from '@/lib/server/bff-http'

/** Proxies GET /api/seller/analytics/overview with session cookies. */
export async function GET(request: Request) {
  const query = analyticsOverviewBackendQuery(new URL(request.url))
  if (!query.ok) return query.response

  const store = await cookies()
  const backendPath = `/api/seller/analytics/overview${query.qs}`
  const res = await fetchBackendAuthorized(store, backendPath, {
    method: 'GET',
    signal: request.signal,
  })
  return forwardAuthenticatedBackendResponse(res)
}
