import { updateCollectionSchema } from '@/lib/collections/collection-schemas'
import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function GET(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/seller/collections/${encodeURIComponent(parsedId.value)}`,
    init: { method: 'GET' },
  })
}

export async function PATCH(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  let json: unknown
  try {
    json = await request.json()
  } catch {
    return invalidJsonResponse()
  }
  const parsed = updateCollectionSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/seller/collections/${encodeURIComponent(parsedId.value)}`,
    init: {
      method: 'PATCH',
      body: JSON.stringify({
        title: parsed.data.title,
        description: parsed.data.description?.trim() ? parsed.data.description.trim() : null,
      }),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}
