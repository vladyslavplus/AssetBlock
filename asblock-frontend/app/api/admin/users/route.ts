import { z } from 'zod'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { zodValidationProblemResponse } from '@/lib/server/bff-http'

const adminUsersQuerySchema = z.object({
  search: z.string().trim().max(200).optional(),
  page: z.coerce.number().int().min(1).optional(),
  pageSize: z.coerce.number().int().min(1).max(100).optional(),
})

export async function GET(request: Request) {
  const url = new URL(request.url)
  const queryResult = adminUsersQuerySchema.safeParse(
    Object.fromEntries(url.searchParams.entries()),
  )
  if (!queryResult.success) {
    return zodValidationProblemResponse(queryResult.error)
  }

  const { search, page, pageSize } = queryResult.data
  const qs = new URLSearchParams()
  if (search) qs.set('search', search)
  if (page !== undefined) qs.set('page', String(page))
  if (pageSize !== undefined) qs.set('pageSize', String(pageSize))

  const path = qs.size > 0 ? `/api/admin/users?${qs.toString()}` : '/api/admin/users'
  return proxyAuthenticatedBff(request, { path, init: { method: 'GET' } })
}
