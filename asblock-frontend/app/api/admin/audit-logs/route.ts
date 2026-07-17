import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

export async function GET(request: Request) {
  const store = await cookies()
  const url = new URL(request.url)
  const qs = url.searchParams.toString()
  const path = `/api/admin/audit-logs${qs ? `?${qs}` : ''}`
  const res = await fetchBackendAuthorized(store, path, { method: 'GET' })
  return forwardBackendResponse(res)
}
