import { cookies } from 'next/headers'
import { reviseBundleSchema } from '@/lib/bundles/bundle-schemas'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardBackendResponse,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'

export async function GET(_request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const store = await cookies()
  const res = await fetchBackendAuthorized(store, `/api/seller/bundles/${encodeURIComponent(id)}`, {
    method: 'GET',
  })
  return forwardBackendResponse(res)
}

export async function PUT(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id } = await context.params
  let json: unknown
  try {
    json = await request.json()
  } catch {
    return invalidJsonResponse()
  }
  const parsed = reviseBundleSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(store, `/api/seller/bundles/${encodeURIComponent(id)}`, {
    method: 'PUT',
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
