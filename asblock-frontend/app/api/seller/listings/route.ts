import { cookies } from 'next/headers'
import { z } from 'zod'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  forwardAuthenticatedBackendResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'

const sellerListingsQuerySchema = z
  .object({
    page: z.coerce.number().int().min(1).optional(),
    pageSize: z.coerce.number().int().min(1).max(100).optional(),
    sortBy: z.enum(['Title', 'Price', 'CreatedAt', 'Id']).optional(),
    sortDirection: z.enum(['ASC', 'DESC']).optional(),
    search: z.string().trim().max(200).optional(),
    minPrice: z.coerce.number().min(0).optional(),
    maxPrice: z.coerce.number().min(0).optional(),
  })
  .refine(
    (data) =>
      data.minPrice === undefined || data.maxPrice === undefined || data.minPrice <= data.maxPrice,
    {
      message: 'minPrice must be less than or equal to maxPrice.',
      path: ['minPrice'],
    },
  )

/**
 * Proxies GET /api/users/me/assets (seller's listed assets) with validated query parameters.
 */
export async function GET(request: Request) {
  const url = new URL(request.url)
  const queryResult = sellerListingsQuerySchema.safeParse(
    Object.fromEntries(url.searchParams.entries()),
  )
  if (!queryResult.success) {
    return zodValidationProblemResponse(queryResult.error)
  }

  const { page, pageSize, sortBy, sortDirection, search, minPrice, maxPrice } = queryResult.data

  const qs = new URLSearchParams()
  if (page !== undefined) qs.set('page', String(page))
  if (pageSize !== undefined) qs.set('pageSize', String(pageSize))
  if (sortDirection) qs.set('sortDirection', sortDirection)
  if (sortBy) qs.set('sortBy', sortBy)
  if (search) qs.set('search', search)
  if (minPrice !== undefined) qs.set('minPrice', String(minPrice))
  if (maxPrice !== undefined) qs.set('maxPrice', String(maxPrice))

  const store = await cookies()
  const backendPath = qs.size > 0 ? `/api/users/me/assets?${qs.toString()}` : '/api/users/me/assets'
  const res = await fetchBackendAuthorized(store, backendPath, {
    method: 'GET',
    signal: request.signal,
  })
  return forwardAuthenticatedBackendResponse(res)
}
