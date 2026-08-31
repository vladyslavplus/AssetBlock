import { cookies } from 'next/headers'

import { analyticsSalesExportBackendQuery } from '@/lib/server/analytics-bff-params'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendDownloadResponse } from '@/lib/server/bff-http'
import { LONG_RUNNING_BACKEND_TIMEOUT_MS } from '@/lib/server/fetch-backend'

/** Proxies GET /api/seller/analytics/sales/export with session cookies; streams CSV body. */
export async function GET(request: Request) {
  const query = analyticsSalesExportBackendQuery(new URL(request.url))
  if (!query.ok) return query.response

  const store = await cookies()
  const backendPath = `/api/seller/analytics/sales/export${query.qs}`
  const res = await fetchBackendAuthorized(
    store,
    backendPath,
    {
      method: 'GET',
      signal: request.signal,
    },
    { timeoutMs: LONG_RUNNING_BACKEND_TIMEOUT_MS },
  )
  return forwardBackendDownloadResponse(res)
}
