import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardAuthenticatedBackendResponse,
  problemResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'
import { LONG_RUNNING_BACKEND_TIMEOUT_MS } from '@/lib/server/fetch-backend'
import {
  buildPublishVersionForwardForm,
  parsePublishVersionMultipart,
} from '@/lib/seller/seller-multipart-schemas'

/**
 * Proxies multipart POST to AssetBlock POST /api/assets/{id}/versions (author only).
 * Rebuilds FormData from whitelisted fields after Zod validation.
 */
export const maxDuration = 300

export async function POST(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const incoming = await request.formData()
  const { parsed, file, fileError } = parsePublishVersionMultipart(incoming)

  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }
  if (!file || fileError) {
    return problemResponse(400, 'ERR_VALIDATION_FAILED', fileError ?? 'Choose a file to upload.', {
      file: [fileError ?? 'Choose a file to upload.'],
    })
  }

  const store = await cookies()
  const forwardBody = buildPublishVersionForwardForm(parsed.data, file)
  const res = await fetchBackendAuthorized(
    store,
    `/api/assets/${encodeURIComponent(parsedId.value)}/versions`,
    {
      method: 'POST',
      body: forwardBody,
      signal: request.signal,
    },
    { timeoutMs: LONG_RUNNING_BACKEND_TIMEOUT_MS },
  )
  return forwardAuthenticatedBackendResponse(res)
}
