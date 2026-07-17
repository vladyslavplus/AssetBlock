'use client'

import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useState } from 'react'

import {
  ADMIN_AUDIT_LOGS_PAGE_SIZE,
  adminAuditKeys,
  fetchAuditLogsAdminPage,
  type AuditActorType,
  type AuditLogsAdminFilters,
  type AuditOutcome,
} from '@/lib/admin/admin-audit-query'

function toIsoStartOfDayLocal(date: string): string | undefined {
  const trimmed = date.trim()
  if (!trimmed) return undefined
  const parsed = new Date(`${trimmed}T00:00:00`)
  if (Number.isNaN(parsed.getTime())) return undefined
  return parsed.toISOString()
}

function toIsoEndOfDayLocal(date: string): string | undefined {
  const trimmed = date.trim()
  if (!trimmed) return undefined
  const parsed = new Date(`${trimmed}T23:59:59.999`)
  if (Number.isNaN(parsed.getTime())) return undefined
  return parsed.toISOString()
}

function defaultDateRange(): { from: string; to: string } {
  const to = new Date()
  const from = new Date()
  from.setDate(to.getDate() - 6)
  return { from: toLocalDateInput(from), to: toLocalDateInput(to) }
}

function toLocalDateInput(date: Date): string {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

export interface AuditLogsDraftFilters {
  from: string
  to: string
  actorUserId: string
  actorType: AuditActorType | ''
  action: string
  outcome: AuditOutcome | ''
  resourceType: string
  resourceId: string
}

function emptyDraft(range = defaultDateRange()): AuditLogsDraftFilters {
  return {
    from: range.from,
    to: range.to,
    actorUserId: '',
    actorType: '',
    action: '',
    outcome: '',
    resourceType: '',
    resourceId: '',
  }
}

function toAppliedFilters(draft: AuditLogsDraftFilters, page: number): AuditLogsAdminFilters {
  return {
    page,
    pageSize: ADMIN_AUDIT_LOGS_PAGE_SIZE,
    actorUserId: draft.actorUserId.trim() || undefined,
    actorType: draft.actorType || undefined,
    action: draft.action.trim() || undefined,
    outcome: draft.outcome || undefined,
    resourceType: draft.resourceType.trim() || undefined,
    resourceId: draft.resourceId.trim() || undefined,
    from: toIsoStartOfDayLocal(draft.from),
    to: toIsoEndOfDayLocal(draft.to),
  }
}

export function useAdminAuditLogs() {
  const [draft, setDraft] = useState<AuditLogsDraftFilters>(emptyDraft)
  const [applied, setApplied] = useState<AuditLogsAdminFilters>(() => toAppliedFilters(draft, 1))
  const [expandedId, setExpandedId] = useState<number | null>(null)

  const listQuery = useQuery({
    queryKey: adminAuditKeys.list(applied),
    queryFn: () => fetchAuditLogsAdminPage(applied),
    placeholderData: keepPreviousData,
  })

  const applyFilters = () => {
    setExpandedId(null)
    setApplied(toAppliedFilters(draft, 1))
  }

  const resetFilters = () => {
    const next = emptyDraft()
    setDraft(next)
    setExpandedId(null)
    setApplied(toAppliedFilters(next, 1))
  }

  const setPage = (page: number) => {
    setExpandedId(null)
    setApplied((prev) => ({ ...prev, page: Math.max(1, page) }))
  }

  const pageData = listQuery.data
  const totalCount = pageData?.totalCount ?? 0
  const pageSize = pageData?.pageSize ?? ADMIN_AUDIT_LOGS_PAGE_SIZE
  const page = applied.page
  const totalPages = totalCount === 0 ? 0 : Math.ceil(totalCount / pageSize)

  return {
    draft,
    setDraft,
    applyFilters,
    resetFilters,
    page,
    setPage,
    expandedId,
    setExpandedId,
    listQuery,
    rows: pageData?.items ?? [],
    totalCount,
    totalPages,
    rangeStart: totalCount === 0 ? 0 : (page - 1) * pageSize + 1,
    rangeEnd: totalCount === 0 ? 0 : Math.min(page * pageSize, totalCount),
  }
}

export type AdminAuditLogsController = ReturnType<typeof useAdminAuditLogs>
