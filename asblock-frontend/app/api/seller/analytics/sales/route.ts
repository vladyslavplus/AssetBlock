import { cookies } from 'next/headers'

import { analyticsSalesBackendQuery } from '@/lib/server/analytics-bff-params'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

/** Proxies GET /api/seller/analytics/sales with session cookies. */
export async function GET(request: Request) {
  const query = analyticsSalesBackendQuery(new URL(request.url))
  if (!query.ok) return query.response

  const store = await cookies()
  const backendPath = `/api/seller/analytics/sales${query.qs}`
  const res = await fetchBackendAuthorized(store, backendPath, { method: 'GET' })
  return forwardBackendResponse(res)
}
