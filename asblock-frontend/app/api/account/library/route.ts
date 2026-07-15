import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

export async function GET() {
  const store = await cookies()
  const qs = new URLSearchParams({
    page: '1',
    pageSize: '100',
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
