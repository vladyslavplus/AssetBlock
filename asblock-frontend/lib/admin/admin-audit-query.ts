import type { PagedResultDto } from '@/lib/catalog/assets-api'
import { ApiRequestError } from '@/lib/http/api-client'
import { getApiErrorMessage, readApiResponseBody } from '@/lib/http/api-errors'

export type AuditActorType = 'USER' | 'SYSTEM' | 'ANONYMOUS'
export type AuditOutcome = 'SUCCESS' | 'FAILURE' | 'DENIED'

export interface AuditLogListItemApi {
  id: number
  occurredAt: string
  actorType: AuditActorType
  actorUserId: string | null
  action: string
  outcome: AuditOutcome
  resourceType: string
  resourceId: string | null
  traceId: string | null
  ipAddress: string | null
  userAgent: string | null
  metadata: Record<string, unknown> | null
}

export interface AuditLogsAdminFilters {
  page: number
  pageSize?: number
  actorUserId?: string
  actorType?: AuditActorType | ''
  action?: string
  outcome?: AuditOutcome | ''
  resourceType?: string
  resourceId?: string
  from?: string
  to?: string
}

export const ADMIN_AUDIT_LOGS_PAGE_SIZE = 20

export const adminAuditKeys = {
  all: ['admin', 'audit-logs'] as const,
  list: (filters: AuditLogsAdminFilters) => [...adminAuditKeys.all, 'list', filters] as const,
}

function buildQuery(params: AuditLogsAdminFilters): string {
  const pageSize = params.pageSize ?? ADMIN_AUDIT_LOGS_PAGE_SIZE
  const qs = new URLSearchParams({
    page: String(Math.max(1, params.page)),
    pageSize: String(pageSize),
  })

  const actorUserId = params.actorUserId?.trim()
  if (actorUserId) qs.set('actorUserId', actorUserId)

  if (params.actorType) qs.set('actorType', params.actorType)
  if (params.outcome) qs.set('outcome', params.outcome)

  const action = params.action?.trim()
  if (action) qs.set('action', action)

  const resourceType = params.resourceType?.trim()
  if (resourceType) qs.set('resourceType', resourceType)

  const resourceId = params.resourceId?.trim()
  if (resourceId) qs.set('resourceId', resourceId)

  if (params.from) qs.set('from', params.from)
  if (params.to) qs.set('to', params.to)

  return qs.toString()
}

/** Authenticated admin read via same-origin BFF (httpOnly cookies). */
export async function fetchAuditLogsAdminPage(
  params: AuditLogsAdminFilters,
): Promise<PagedResultDto<AuditLogListItemApi>> {
  const res = await fetch(`/api/admin/audit-logs?${buildQuery(params)}`, {
    credentials: 'include',
    cache: 'no-store',
  })
  const body = await readApiResponseBody(res)
  if (!res.ok) {
    throw new ApiRequestError(
      getApiErrorMessage(body, `Request failed (${res.status})`),
      res.status,
      body,
    )
  }
  return body as PagedResultDto<AuditLogListItemApi>
}
