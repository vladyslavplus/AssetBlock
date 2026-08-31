import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendDownloadResponse } from '@/lib/server/bff-http'
import { parseOptionalUuidParam, parseUuidParam } from '@/lib/server/bff-params'
import { LONG_RUNNING_BACKEND_TIMEOUT_MS } from '@/lib/server/fetch-backend'

export async function GET(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const url = new URL(request.url)
  const rawVersionId = url.searchParams.get('versionId')
  const parsedVersionId = parseOptionalUuidParam('versionId', rawVersionId)
  if (!parsedVersionId.ok) {
    return parsedVersionId.response
  }

  const store = await cookies()
  const path = parsedVersionId.value
    ? `/api/assets/${encodeURIComponent(parsedId.value)}/versions/${encodeURIComponent(parsedVersionId.value)}/download`
    : `/api/assets/${encodeURIComponent(parsedId.value)}/download`

  const res = await fetchBackendAuthorized(
    store,
    path,
    { method: 'GET', signal: request.signal },
    { timeoutMs: LONG_RUNNING_BACKEND_TIMEOUT_MS },
  )

  return forwardBackendDownloadResponse(res)
}
