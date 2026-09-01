import { analyticsProductsBackendQuery } from '@/lib/server/analytics-bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

/** Proxies GET /api/seller/analytics/products with session cookies. */
export async function GET(request: Request) {
  const query = analyticsProductsBackendQuery(new URL(request.url))
  if (!query.ok) return query.response

  const backendPath = `/api/seller/analytics/products${query.qs}`
  return proxyAuthenticatedBff(request, { path: backendPath, init: { method: 'GET' } })
}
