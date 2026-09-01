import { analyticsProductDetailBackendQuery } from '@/lib/server/analytics-bff-params'
import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

/** Proxies GET /api/seller/analytics/products/assets/{id} with session cookies. */
export async function GET(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) return parsedId.response

  const query = analyticsProductDetailBackendQuery(new URL(request.url))
  if (!query.ok) return query.response

  const backendPath = `/api/seller/analytics/products/assets/${encodeURIComponent(parsedId.value)}${query.qs}`
  return proxyAuthenticatedBff(request, { path: backendPath, init: { method: 'GET' } })
}
