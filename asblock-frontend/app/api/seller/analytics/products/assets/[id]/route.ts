import { cookies } from 'next/headers'

import { analyticsProductDetailBackendQuery } from '@/lib/server/analytics-bff-params'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

/** Proxies GET /api/seller/analytics/products/assets/{id} with session cookies. */
export async function GET(request: Request, context: { params: Promise<{ id: string }> }) {
  const query = analyticsProductDetailBackendQuery(new URL(request.url))
  if (!query.ok) return query.response

  const { id } = await context.params
  const store = await cookies()
  const backendPath = `/api/seller/analytics/products/assets/${encodeURIComponent(id)}${query.qs}`
  const res = await fetchBackendAuthorized(store, backendPath, { method: 'GET' })
  return forwardBackendResponse(res)
}
