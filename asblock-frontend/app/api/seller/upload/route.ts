import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardBackendResponse,
  problemResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import {
  buildAssetUploadForwardForm,
  parseAssetUploadMultipart,
} from '@/lib/seller/seller-multipart-schemas'

/**
 * Proxies multipart POST to AssetBlock POST /api/assets/upload (Bearer from cookies).
 * Rebuilds FormData from whitelisted fields after Zod validation.
 */
export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const incoming = await request.formData()
  const { parsed, file, fileError } = parseAssetUploadMultipart(incoming)

  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }
  if (!file || fileError) {
    return problemResponse(400, 'ERR_VALIDATION_FAILED', fileError ?? 'Choose a file to upload.', {
      file: [fileError ?? 'Choose a file to upload.'],
    })
  }

  const store = await cookies()
  const forwardBody = buildAssetUploadForwardForm(parsed.data, file)
  const res = await fetchBackendAuthorized(store, '/api/assets/upload', {
    method: 'POST',
    body: forwardBody,
  })
  return forwardBackendResponse(res)
}
