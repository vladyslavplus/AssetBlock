import { cookies } from 'next/headers'
import { reorderCollectionItemsSchema } from '@/lib/collections/collection-schemas'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardBackendResponse,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'

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
  const parsed = reorderCollectionItemsSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/seller/collections/${encodeURIComponent(id)}/items/order`,
    {
      method: 'PUT',
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
  )
  return forwardBackendResponse(res)
}
