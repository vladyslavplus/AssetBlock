import { cookies } from 'next/headers'
import { z } from 'zod'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  forwardAuthenticatedBackendResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'

const adminAuditLogsQuerySchema = z.object({
  page: z.coerce.number().int().min(1).optional(),
  pageSize: z.coerce.number().int().min(1).max(100).optional(),
  actorUserId: z.string().uuid().optional(),
  actorType: z.enum(['USER', 'SYSTEM', 'ANONYMOUS']).optional(),
  action: z.string().trim().max(100).optional(),
  outcome: z.enum(['SUCCESS', 'FAILURE', 'DENIED']).optional(),
  resourceType: z.string().trim().max(100).optional(),
  resourceId: z.string().trim().max(200).optional(),
  from: z.string().datetime({ offset: true }).optional(),
  to: z.string().datetime({ offset: true }).optional(),
})

export async function GET(request: Request) {
  const url = new URL(request.url)
  const queryResult = adminAuditLogsQuerySchema.safeParse(
    Object.fromEntries(url.searchParams.entries()),
  )
  if (!queryResult.success) {
    return zodValidationProblemResponse(queryResult.error)
  }

  const {
    page,
    pageSize,
    actorUserId,
    actorType,
    action,
    outcome,
    resourceType,
    resourceId,
    from,
    to,
  } = queryResult.data

  const qs = new URLSearchParams()
  if (page !== undefined) qs.set('page', String(page))
  if (pageSize !== undefined) qs.set('pageSize', String(pageSize))
  if (actorUserId) qs.set('actorUserId', actorUserId)
  if (actorType) qs.set('actorType', actorType)
  if (action) qs.set('action', action)
  if (outcome) qs.set('outcome', outcome)
  if (resourceType) qs.set('resourceType', resourceType)
  if (resourceId) qs.set('resourceId', resourceId)
  if (from) qs.set('from', from)
  if (to) qs.set('to', to)

  const store = await cookies()
  const path = qs.size > 0 ? `/api/admin/audit-logs?${qs.toString()}` : '/api/admin/audit-logs'
  const res = await fetchBackendAuthorized(store, path, {
    method: 'GET',
    signal: request.signal,
  })
  return forwardAuthenticatedBackendResponse(res)
}
