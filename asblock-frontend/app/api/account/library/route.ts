import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

export async function GET(request: Request) {
  const store = await cookies()
  const url = new URL(request.url)
  const page = Math.max(1, parseInt(url.searchParams.get('page') || '1', 10) || 1)
  const requestedPageSize = parseInt(url.searchParams.get('pageSize') || '20', 10) || 20
  const pageSize = Math.min(100, Math.max(1, requestedPageSize))

  const qs = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
    sortDirection: 'DESC',
  })
  const res = await fetchBackendAuthorized(
    store,
    `/api/users/me/purchases?${qs.toString()}`,
    { method: 'GET' },
    { persistRefreshedTokens: false },
  )
  return forwardBackendResponse(res)
}
