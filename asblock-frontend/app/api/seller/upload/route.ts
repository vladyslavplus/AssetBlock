import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'

/**
 * Proxies multipart POST to AssetBlock POST /api/assets/upload (Bearer from cookies).
 */
export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const store = await cookies()
  const formData = await request.formData()
  const res = await fetchBackendAuthorized(store, '/api/assets/upload', {
    method: 'POST',
    body: formData,
  })
  return forwardBackendResponse(res)
}
