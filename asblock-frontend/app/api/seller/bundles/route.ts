import { cookies } from 'next/headers'
import { z } from 'zod'
import { createBundleSchema } from '@/lib/bundles/bundle-schemas'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardAuthenticatedBackendResponse,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'

const sellerBundlesQuerySchema = z.object({
  page: z.coerce.number().int().min(1).optional(),
  pageSize: z.coerce.number().int().min(1).max(100).optional(),
  sortBy: z.enum(['UpdatedAt', 'CreatedAt', 'Title', 'Price']).optional(),
  sortDirection: z.enum(['ASC', 'DESC']).optional(),
  search: z.string().trim().max(200).optional(),
  archivedOnly: z
    .enum(['true', 'false'])
    .transform((val) => val === 'true')
    .optional(),
})

export async function GET(request: Request) {
  const url = new URL(request.url)
  const queryResult = sellerBundlesQuerySchema.safeParse(
    Object.fromEntries(url.searchParams.entries()),
  )
  if (!queryResult.success) {
    return zodValidationProblemResponse(queryResult.error)
  }

  const { page, pageSize, sortBy, sortDirection, search, archivedOnly } = queryResult.data
  const qs = new URLSearchParams()
  if (page !== undefined) qs.set('page', String(page))
  if (pageSize !== undefined) qs.set('pageSize', String(pageSize))
  if (sortDirection) qs.set('sortDirection', sortDirection)
  if (sortBy) qs.set('sortBy', sortBy)
  if (search) qs.set('search', search)
  if (archivedOnly !== undefined) qs.set('archivedOnly', String(archivedOnly))

  const store = await cookies()
  const backendPath = qs.size > 0 ? `/api/seller/bundles?${qs.toString()}` : '/api/seller/bundles'
  const res = await fetchBackendAuthorized(store, backendPath, {
    method: 'GET',
    signal: request.signal,
  })
  return forwardAuthenticatedBackendResponse(res)
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
    signal: request.signal,
  })
  return forwardAuthenticatedBackendResponse(res)
}
