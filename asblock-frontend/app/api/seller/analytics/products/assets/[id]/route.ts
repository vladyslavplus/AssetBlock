import { cookies } from 'next/headers'

import { analyticsProductDetailBackendQuery } from '@/lib/server/analytics-bff-params'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardAuthenticatedBackendResponse } from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'

/** Proxies GET /api/seller/analytics/products/assets/{id} with session cookies. */
export async function GET(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) return parsedId.response

  const query = analyticsProductDetailBackendQuery(new URL(request.url))
  if (!query.ok) return query.response

  const store = await cookies()
  const backendPath = `/api/seller/analytics/products/assets/${encodeURIComponent(parsedId.value)}${query.qs}`
  const res = await fetchBackendAuthorized(store, backendPath, {
    method: 'GET',
    signal: request.signal,
  })
  return forwardAuthenticatedBackendResponse(res)
}
