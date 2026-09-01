import { analyticsOverviewBackendQuery } from '@/lib/server/analytics-bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

/** Proxies GET /api/seller/analytics/overview with session cookies. */
export async function GET(request: Request) {
  const query = analyticsOverviewBackendQuery(new URL(request.url))
  if (!query.ok) return query.response

  const backendPath = `/api/seller/analytics/overview${query.qs}`
  return proxyAuthenticatedBff(request, { path: backendPath, init: { method: 'GET' } })
}
