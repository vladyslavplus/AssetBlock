import { analyticsSalesBackendQuery } from '@/lib/server/analytics-bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

/** Proxies GET /api/seller/analytics/sales with session cookies. */
export async function GET(request: Request) {
  const query = analyticsSalesBackendQuery(new URL(request.url))
  if (!query.ok) return query.response

  const backendPath = `/api/seller/analytics/sales${query.qs}`
  return proxyAuthenticatedBff(request, { path: backendPath, init: { method: 'GET' } })
}
