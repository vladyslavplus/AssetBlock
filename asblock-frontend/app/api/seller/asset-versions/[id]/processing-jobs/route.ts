import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

export async function GET(_request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/users/me/asset-versions/${encodeURIComponent(id)}/processing-jobs`,
    { method: 'GET' },
  )
  return forwardBackendResponse(res)
}
