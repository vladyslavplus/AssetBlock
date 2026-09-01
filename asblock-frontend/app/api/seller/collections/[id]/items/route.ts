import { addCollectionItemSchema } from '@/lib/collections/collection-schemas'
import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function POST(request: Request, context: { params: Promise<{ id: string }> }) {
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
  const parsed = addCollectionItemSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/seller/collections/${encodeURIComponent(parsedId.value)}/items`,
    init: {
      method: 'POST',
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}
