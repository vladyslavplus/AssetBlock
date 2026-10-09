import type { PagedResultDto } from '@/lib/catalog/assets-api'
import { z } from 'zod'
import { ApiRequestError } from '@/lib/http/api-client'
import { getApiErrorMessage, readApiResponseBody } from '@/lib/http/api-errors'
import {
  adminUserListItemSchema,
  adminUserRoleAssignmentResultSchema,
  adminUserRoleUpdateSchema,
  type AdminUserListItem,
  type AdminUserRoleAssignmentResult,
} from '@/lib/admin/admin-schemas'

export const ADMIN_USERS_PAGE_SIZE = 20

export const adminUserKeys = {
  all: ['admin', 'users'] as const,
  list: (params: { search: string; page: number }) =>
    [...adminUserKeys.all, 'list', params] as const,
}

function buildUsersQuery(params: { search?: string; page: number; pageSize?: number }): string {
  const qs = new URLSearchParams({
    page: String(Math.max(1, params.page)),
    pageSize: String(params.pageSize ?? ADMIN_USERS_PAGE_SIZE),
  })
  const search = params.search?.trim()
  if (search) qs.set('search', search)
  return qs.toString()
}

export async function fetchAdminUsersPage(params: {
  search?: string
  page: number
  pageSize?: number
  signal?: AbortSignal
}): Promise<PagedResultDto<AdminUserListItem>> {
  const res = await fetch(`/api/admin/users?${buildUsersQuery(params)}`, {
    credentials: 'include',
    cache: 'no-store',
    signal: params.signal,
  })
  const body: unknown = await readApiResponseBody(res)
  if (!res.ok) {
    throw new ApiRequestError(
      getApiErrorMessage(body, `Could not load users (${res.status})`),
      res.status,
      body,
    )
  }
  const parsed = z
    .object({
      items: z.array(adminUserListItemSchema),
      totalCount: z.number().int().nonnegative(),
      page: z.number().int().positive(),
      pageSize: z.number().int().positive(),
      totalPages: z.number().int().nonnegative().optional(),
    })
    .safeParse(body)
  if (!parsed.success) {
    throw new ApiRequestError('Users response was invalid.', res.status, body)
  }
  return parsed.data
}

export type AssignUserRoleOutcome =
  | { ok: true; result: AdminUserRoleAssignmentResult }
  | { ok: false; message: string; roleRevisionStale: boolean }

export async function assignAdminUserRole(params: {
  userId: string
  role: 'User' | 'Moderator'
  expectedRoleRevision: number
}): Promise<AssignUserRoleOutcome> {
  const body = adminUserRoleUpdateSchema.parse({
    role: params.role,
    expectedRoleRevision: params.expectedRoleRevision,
  })
  const res = await fetch(`/api/admin/users/${encodeURIComponent(params.userId)}/role`, {
    method: 'PATCH',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const parsedBody: unknown = await readApiResponseBody(res)
  if (!res.ok) {
    const message = getApiErrorMessage(parsedBody, `Could not update role (${res.status})`)
    const code =
      typeof parsedBody === 'object' && parsedBody !== null && 'code' in parsedBody
        ? (parsedBody as { code?: unknown }).code
        : undefined
    return {
      ok: false,
      message,
      roleRevisionStale:
        res.status === 409 && (code === 'ERR_ROLE_REVISION_STALE' || code === undefined),
    }
  }
  const parsed = adminUserRoleAssignmentResultSchema.safeParse(parsedBody)
  if (!parsed.success) {
    return { ok: false, message: 'Unexpected response from server.', roleRevisionStale: false }
  }
  return { ok: true, result: parsed.data }
}
