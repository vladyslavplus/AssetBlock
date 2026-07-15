import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'

export async function POST(request: Request, context: { params: Promise<{ assetId: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { assetId } = await context.params
  const store = await cookies()
  const body = await request.text()
  const path = `/api/reviews/assets/${encodeURIComponent(assetId)}/reviews`
  const res = await fetchBackendAuthorized(store, path, {
    method: 'POST',
    body,
    headers: { 'Content-Type': 'application/json' },
  })
  return forwardBackendResponse(res)
}
