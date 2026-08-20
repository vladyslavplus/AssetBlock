import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

export async function GET(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const store = await cookies()
  const url = new URL(request.url)
  const versionId = url.searchParams.get('versionId')?.trim()

  const path =
    versionId && versionId.length > 0
      ? `/api/assets/${encodeURIComponent(id)}/versions/${encodeURIComponent(versionId)}/download`
      : `/api/assets/${encodeURIComponent(id)}/download`

  const res = await fetchBackendAuthorized(store, path, { method: 'GET' })

  return forwardBackendResponse(res)
}
