import { cookies } from 'next/headers'
import { z } from 'zod'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  forwardAuthenticatedBackendResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'

const notificationsQuerySchema = z.object({
  page: z.coerce.number().int().min(1).optional(),
  pageSize: z.coerce.number().int().min(1).max(100).optional(),
  sortBy: z.enum(['CreatedAt', 'ReadAt']).optional(),
  sortDirection: z.enum(['ASC', 'DESC']).optional(),
  unreadOnly: z
    .enum(['true', 'false'])
    .transform((val) => val === 'true')
    .optional(),
})

export async function GET(request: Request) {
  const url = new URL(request.url)
  const queryResult = notificationsQuerySchema.safeParse(
    Object.fromEntries(url.searchParams.entries()),
  )
  if (!queryResult.success) {
    return zodValidationProblemResponse(queryResult.error)
  }

  const { page, pageSize, sortBy, sortDirection, unreadOnly } = queryResult.data
  const qs = new URLSearchParams()
  if (page !== undefined) qs.set('page', String(page))
  if (pageSize !== undefined) qs.set('pageSize', String(pageSize))
  if (sortDirection) qs.set('sortDirection', sortDirection)
  if (sortBy) qs.set('sortBy', sortBy)
  if (unreadOnly !== undefined) qs.set('unreadOnly', String(unreadOnly))

  const store = await cookies()
  const path =
    qs.size > 0 ? `/api/users/me/notifications?${qs.toString()}` : '/api/users/me/notifications'
  const res = await fetchBackendAuthorized(store, path, {
    method: 'GET',
    signal: request.signal,
  })
  return forwardAuthenticatedBackendResponse(res)
}
