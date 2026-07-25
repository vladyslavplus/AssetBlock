import { cookies } from 'next/headers'
import { createBundleSchema } from '@/lib/bundles/bundle-schemas'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardBackendResponse,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'

export async function GET(request: Request) {
  const store = await cookies()
  const url = new URL(request.url)
  const qs = url.searchParams.toString()
  const backendPath = `/api/seller/bundles${qs ? `?${qs}` : ''}`
  const res = await fetchBackendAuthorized(store, backendPath, { method: 'GET' })
  return forwardBackendResponse(res)
}

export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  let json: unknown
  try {
    json = await request.json()
  } catch {
    return invalidJsonResponse()
  }
  const parsed = createBundleSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(store, '/api/seller/bundles', {
    method: 'POST',
    body: JSON.stringify({
      title: parsed.data.title,
      description: parsed.data.description?.trim() ? parsed.data.description.trim() : null,
      price: parsed.data.price,
      assetIds: parsed.data.assetIds,
    }),
    headers: { 'Content-Type': 'application/json' },
  })
  return forwardBackendResponse(res)
}
