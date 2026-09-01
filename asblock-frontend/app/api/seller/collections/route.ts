import { z } from 'zod'
import { createCollectionSchema } from '@/lib/collections/collection-schemas'
import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

const sellerCollectionsQuerySchema = z.object({
  page: z.coerce.number().int().min(1).optional(),
  pageSize: z.coerce.number().int().min(1).max(100).optional(),
  sortBy: z.enum(['UpdatedAt', 'CreatedAt', 'Title', 'Status']).optional(),
  sortDirection: z.enum(['ASC', 'DESC']).optional(),
  status: z.enum(['DRAFT', 'PUBLISHED', 'ARCHIVED']).optional(),
  search: z.string().trim().max(200).optional(),
})

export async function GET(request: Request) {
  const url = new URL(request.url)
  const queryResult = sellerCollectionsQuerySchema.safeParse(
    Object.fromEntries(url.searchParams.entries()),
  )
  if (!queryResult.success) {
    return zodValidationProblemResponse(queryResult.error)
  }

  const { page, pageSize, sortBy, sortDirection, status, search } = queryResult.data
  const qs = new URLSearchParams()
  if (page !== undefined) qs.set('page', String(page))
  if (pageSize !== undefined) qs.set('pageSize', String(pageSize))
  if (sortDirection) qs.set('sortDirection', sortDirection)
  if (sortBy) qs.set('sortBy', sortBy)
  if (status) qs.set('status', status)
  if (search) qs.set('search', search)

  const backendPath =
    qs.size > 0 ? `/api/seller/collections?${qs.toString()}` : '/api/seller/collections'
  return proxyAuthenticatedBff(request, { path: backendPath, init: { method: 'GET' } })
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
  const parsed = createCollectionSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  return proxyAuthenticatedBff(request, {
    path: '/api/seller/collections',
    init: {
      method: 'POST',
      body: JSON.stringify({
        title: parsed.data.title,
        description: parsed.data.description?.trim() ? parsed.data.description.trim() : null,
      }),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}
