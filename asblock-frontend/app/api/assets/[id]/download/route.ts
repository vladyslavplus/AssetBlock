import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

export async function GET(_request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const store = await cookies()
  const path = `/api/assets/${encodeURIComponent(id)}/download`
  const res = await fetchBackendAuthorized(store, path, { method: 'GET' })

  return forwardBackendResponse(res)
}
