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
import { prepareMultipartUpload } from '@/lib/server/multipart-upload'
import {
  parsePublishVersionMetadata,
  validateArchiveUploadFilename,
} from '@/lib/seller/seller-multipart-schemas'

/**
 * Proxies multipart POST to AssetBlock POST /api/assets/{id}/versions (author only).
 * Normalizes bounded metadata, then streams the original file bytes without buffering.
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

  const incoming = await prepareMultipartUpload(request)
  if (!incoming.ok) return incoming.response

  const parsed = parsePublishVersionMetadata(incoming.metadata)
  const fileError = validateArchiveUploadFilename(incoming.fileName)

  if (!parsed.success) {
    await incoming.cancel('invalid_metadata')
    return zodValidationProblemResponse(parsed.error)
  }
  if (fileError) {
    await incoming.cancel('invalid_file')
    return problemResponse(400, 'ERR_VALIDATION_FAILED', fileError, {
      file: [fileError],
    })
  }

  const store = await cookies()
  const forward = incoming.createForwardBody([
    ['licenseCode', parsed.data.licenseCode],
    ['releaseNotes', parsed.data.releaseNotes],
  ])
  if (!forward) {
    await incoming.cancel('invalid_multipart_value')
    return problemResponse(
      400,
      'ERR_VALIDATION_FAILED',
      'An upload field contains an invalid multipart boundary sequence.',
      { body: ['Remove the multipart boundary sequence from upload text fields.'] },
    )
  }
  const forwardInit: RequestInit & { duplex: 'half' } = {
    method: 'POST',
    headers: {
      'Content-Type': incoming.contentType,
      'Content-Length': String(forward.contentLength),
    },
    body: forward.body,
    duplex: 'half',
    signal: request.signal,
  }
  let res: Response
  try {
    res = await fetchBackendAuthorized(
      store,
      `/api/assets/${encodeURIComponent(parsedId.value)}/versions`,
      forwardInit,
      {
        timeoutMs: LONG_RUNNING_BACKEND_TIMEOUT_MS,
        retryOnUnauthorized: false,
      },
    )
  } finally {
    if (!forward.body.locked) {
      await forward.body.cancel('forward_complete').catch(() => undefined)
    }
  }
  return forwardAuthenticatedBackendResponse(res)
}
