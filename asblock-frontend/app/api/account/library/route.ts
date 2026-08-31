import { cookies } from 'next/headers'
import { z } from 'zod'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  forwardAuthenticatedBackendResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'

const libraryQuerySchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce.number().int().min(1).max(100).default(20),
  sortDirection: z.enum(['ASC', 'DESC']).default('DESC'),
})

export async function GET(request: Request) {
  const url = new URL(request.url)
  const queryResult = libraryQuerySchema.safeParse(Object.fromEntries(url.searchParams.entries()))
  if (!queryResult.success) {
    return zodValidationProblemResponse(queryResult.error)
  }

  const { page, pageSize, sortDirection } = queryResult.data
  const qs = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
    sortDirection,
  })

  const store = await cookies()
  const res = await fetchBackendAuthorized(store, `/api/users/me/purchases?${qs.toString()}`, {
    method: 'GET',
    signal: request.signal,
  })
  return forwardAuthenticatedBackendResponse(res)
}
