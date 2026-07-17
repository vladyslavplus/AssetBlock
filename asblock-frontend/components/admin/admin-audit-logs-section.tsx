'use client'

import { ChevronDown, ChevronLeft, ChevronRight, Copy } from 'lucide-react'
import { toast } from 'sonner'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import type { AuditLogListItemApi, AuditOutcome } from '@/lib/admin/admin-audit-query'
import type { AdminAuditLogsController } from '@/lib/admin/use-admin-audit-logs'
import { ApiRequestError } from '@/lib/http/api-client'
import { getApiErrorMessage } from '@/lib/http/api-errors'
import { cn } from '@/lib/utils'

function outcomeVariant(
  outcome: AuditOutcome,
): 'default' | 'secondary' | 'destructive' | 'outline' {
  switch (outcome) {
    case 'SUCCESS':
      return 'default'
    case 'FAILURE':
      return 'destructive'
    case 'DENIED':
      return 'outline'
    default:
      return 'secondary'
  }
}

function formatTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  return date.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

async function copyText(label: string, value: string) {
  try {
    await navigator.clipboard.writeText(value)
    toast.success(`${label} copied`)
  } catch {
    toast.error(`Could not copy ${label}`)
  }
}

function CopyIdButton({ label, value }: { label: string; value: string }) {
  return (
    <Button
      type="button"
      variant="ghost"
      size="sm"
      className="h-7 px-1.5 text-[10px] text-muted-foreground"
      onClick={() => void copyText(label, value)}
    >
      <Copy className="size-3 mr-1" aria-hidden />
      Copy
    </Button>
  )
}

function ActorCell({ row }: { row: AuditLogListItemApi }) {
  if (row.actorType === 'USER' && row.actorUserId) {
    return (
      <div className="flex flex-col gap-0.5 min-w-0">
        <span className="text-xs font-medium">USER</span>
        <span
          className="font-mono text-[10px] text-muted-foreground truncate"
          title={row.actorUserId}
        >
          {row.actorUserId}
        </span>
      </div>
    )
  }
  return <span className="text-xs font-medium">{row.actorType}</span>
}

function ResourceCell({ row }: { row: AuditLogListItemApi }) {
  return (
    <div className="flex flex-col gap-0.5 min-w-0">
      <span className="text-xs">{row.resourceType}</span>
      {row.resourceId ? (
        <span
          className="font-mono text-[10px] text-muted-foreground truncate"
          title={row.resourceId}
        >
          {row.resourceId}
        </span>
      ) : (
        <span className="text-[10px] text-muted-foreground">—</span>
      )}
    </div>
  )
}

interface AdminAuditLogsSectionProps {
  controller: AdminAuditLogsController
}

export function AdminAuditLogsSection({ controller }: AdminAuditLogsSectionProps) {
  const {
    draft,
    setDraft,
    applyFilters,
    resetFilters,
    page,
    setPage,
    expandedId,
    setExpandedId,
    listQuery,
    rows,
    totalCount,
    totalPages,
    rangeStart,
    rangeEnd,
  } = controller

  const errorMessage = listQuery.isError
    ? listQuery.error instanceof ApiRequestError
      ? getApiErrorMessage(listQuery.error.body, listQuery.error.message)
      : listQuery.error instanceof Error
        ? listQuery.error.message
        : 'Could not load audit logs.'
    : null

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground max-w-3xl">
        Append-only security and business audit trail. Default range is the last 7 days. Apply
        filters explicitly — edits do not fetch until you click Apply.
      </p>

      <form
        className="grid gap-3 rounded-lg border border-border bg-card/40 p-3 sm:grid-cols-2 lg:grid-cols-4"
        onSubmit={(event) => {
          event.preventDefault()
          applyFilters()
        }}
      >
        <div className="space-y-1.5">
          <Label htmlFor="audit-from" className="text-xs">
            From
          </Label>
          <Input
            id="audit-from"
            type="date"
            value={draft.from}
            onChange={(e) => setDraft({ ...draft, from: e.target.value })}
            className="h-9 text-xs"
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="audit-to" className="text-xs">
            To
          </Label>
          <Input
            id="audit-to"
            type="date"
            value={draft.to}
            onChange={(e) => setDraft({ ...draft, to: e.target.value })}
            className="h-9 text-xs"
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="audit-actor-user" className="text-xs">
            Actor user ID
          </Label>
          <Input
            id="audit-actor-user"
            value={draft.actorUserId}
            onChange={(e) => setDraft({ ...draft, actorUserId: e.target.value })}
            placeholder="GUID"
            className="h-9 font-mono text-xs"
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="audit-actor-type" className="text-xs">
            Actor type
          </Label>
          <select
            id="audit-actor-type"
            value={draft.actorType}
            onChange={(e) =>
              setDraft({
                ...draft,
                actorType: e.target.value as typeof draft.actorType,
              })
            }
            className="flex h-9 w-full rounded-md border border-input bg-transparent px-3 text-xs"
          >
            <option value="">Any</option>
            <option value="USER">USER</option>
            <option value="SYSTEM">SYSTEM</option>
            <option value="ANONYMOUS">ANONYMOUS</option>
          </select>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="audit-action" className="text-xs">
            Action
          </Label>
          <Input
            id="audit-action"
            value={draft.action}
            onChange={(e) => setDraft({ ...draft, action: e.target.value })}
            placeholder="Asset.Update"
            className="h-9 font-mono text-xs"
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="audit-outcome" className="text-xs">
            Outcome
          </Label>
          <select
            id="audit-outcome"
            value={draft.outcome}
            onChange={(e) =>
              setDraft({
                ...draft,
                outcome: e.target.value as typeof draft.outcome,
              })
            }
            className="flex h-9 w-full rounded-md border border-input bg-transparent px-3 text-xs"
          >
            <option value="">Any</option>
            <option value="SUCCESS">SUCCESS</option>
            <option value="FAILURE">FAILURE</option>
            <option value="DENIED">DENIED</option>
          </select>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="audit-resource-type" className="text-xs">
            Resource type
          </Label>
          <Input
            id="audit-resource-type"
            value={draft.resourceType}
            onChange={(e) => setDraft({ ...draft, resourceType: e.target.value })}
            placeholder="Asset"
            className="h-9 text-xs"
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="audit-resource-id" className="text-xs">
            Resource ID
          </Label>
          <Input
            id="audit-resource-id"
            value={draft.resourceId}
            onChange={(e) => setDraft({ ...draft, resourceId: e.target.value })}
            placeholder="GUID or id"
            className="h-9 font-mono text-xs"
          />
        </div>
        <div className="flex flex-wrap items-end gap-2 sm:col-span-2 lg:col-span-4">
          <Button type="submit" size="sm">
            Apply
          </Button>
          <Button type="button" size="sm" variant="outline" onClick={resetFilters}>
            Reset
          </Button>
        </div>
      </form>

      {listQuery.isPending && !listQuery.data ? (
        <div className="space-y-2 py-4" aria-busy="true" aria-label="Loading audit logs">
          <div className="h-8 rounded bg-muted/60 animate-pulse" />
          <div className="h-8 rounded bg-muted/40 animate-pulse" />
          <div className="h-8 rounded bg-muted/30 animate-pulse" />
        </div>
      ) : listQuery.isError ? (
        <p className="text-sm text-destructive py-4">{errorMessage}</p>
      ) : (
        <>
          <div className="rounded-lg border border-border overflow-x-auto">
            <Table className="table-fixed min-w-[56rem]">
              <TableHeader>
                <TableRow>
                  <TableHead className="text-xs w-[8%] min-w-[2.5rem]" />
                  <TableHead className="text-xs w-[18%] min-w-[9rem]">Time</TableHead>
                  <TableHead className="text-xs w-[18%] min-w-[9rem]">Actor</TableHead>
                  <TableHead className="text-xs w-[16%] min-w-[8rem]">Action</TableHead>
                  <TableHead className="text-xs w-[10%] min-w-[5.5rem]">Outcome</TableHead>
                  <TableHead className="text-xs w-[18%] min-w-[9rem]">Resource</TableHead>
                  <TableHead className="text-xs w-[12%] min-w-[7rem]">Trace</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.length === 0 ? (
                  <TableRow>
                    <TableCell
                      colSpan={7}
                      className="text-xs text-muted-foreground text-center py-10"
                    >
                      No audit events in this range.
                    </TableCell>
                  </TableRow>
                ) : (
                  rows.map((row) => {
                    const open = expandedId === row.id
                    return (
                      <TableRow key={row.id} className={cn(open && 'bg-muted/20')}>
                        <TableCell className="align-top py-2">
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            className="h-7 w-7 p-0"
                            aria-expanded={open}
                            aria-label={open ? 'Hide details' : 'Show details'}
                            onClick={() => setExpandedId(open ? null : row.id)}
                          >
                            <ChevronDown
                              className={cn('size-3.5 transition-transform', open && 'rotate-180')}
                              aria-hidden
                            />
                          </Button>
                        </TableCell>
                        <TableCell className="align-top py-2 text-xs whitespace-nowrap">
                          {formatTime(row.occurredAt)}
                        </TableCell>
                        <TableCell className="align-top py-2">
                          <ActorCell row={row} />
                        </TableCell>
                        <TableCell className="align-top py-2 font-mono text-[11px]">
                          {row.action}
                        </TableCell>
                        <TableCell className="align-top py-2">
                          <Badge variant={outcomeVariant(row.outcome)}>{row.outcome}</Badge>
                        </TableCell>
                        <TableCell className="align-top py-2">
                          <ResourceCell row={row} />
                        </TableCell>
                        <TableCell className="align-top py-2">
                          {row.traceId ? (
                            <span
                              className="font-mono text-[10px] text-muted-foreground truncate block"
                              title={row.traceId}
                            >
                              {row.traceId}
                            </span>
                          ) : (
                            <span className="text-[10px] text-muted-foreground">—</span>
                          )}
                        </TableCell>
                      </TableRow>
                    )
                  })
                )}
              </TableBody>
            </Table>
          </div>

          {expandedId != null &&
            rows
              .filter((row) => row.id === expandedId)
              .map((row) => (
                <div
                  key={`details-${row.id}`}
                  className="rounded-lg border border-border bg-card/50 p-3 space-y-3 text-xs"
                >
                  <div className="flex flex-wrap gap-2">
                    {row.actorUserId ? (
                      <CopyIdButton label="Actor ID" value={row.actorUserId} />
                    ) : null}
                    {row.resourceId ? (
                      <CopyIdButton label="Resource ID" value={row.resourceId} />
                    ) : null}
                    {row.traceId ? <CopyIdButton label="Trace ID" value={row.traceId} /> : null}
                  </div>
                  <div className="grid gap-2 sm:grid-cols-2">
                    <div>
                      <p className="text-muted-foreground mb-0.5">IP address</p>
                      <p className="font-mono">{row.ipAddress ?? '—'}</p>
                    </div>
                    <div>
                      <p className="text-muted-foreground mb-0.5">User agent</p>
                      <p className="break-all">{row.userAgent ?? '—'}</p>
                    </div>
                  </div>
                  <div>
                    <p className="text-muted-foreground mb-1">Metadata</p>
                    {row.metadata ? (
                      <pre className="overflow-x-auto rounded-md border border-border bg-background/60 p-2 font-mono text-[11px] leading-relaxed">
                        {JSON.stringify(row.metadata, null, 2)}
                      </pre>
                    ) : (
                      <p className="text-muted-foreground">No metadata</p>
                    )}
                  </div>
                </div>
              ))}

          <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
            <p className="text-xs text-muted-foreground">
              {totalCount === 0 ? '0 events' : `Showing ${rangeStart}–${rangeEnd} of ${totalCount}`}
            </p>
            <div className="flex items-center gap-2">
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={page <= 1 || listQuery.isFetching}
                onClick={() => setPage(page - 1)}
              >
                <ChevronLeft className="size-3.5" aria-hidden />
                Prev
              </Button>
              <span className="text-xs text-muted-foreground tabular-nums">
                {totalPages === 0 ? '0 / 0' : `${page} / ${totalPages}`}
              </span>
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={totalPages === 0 || page >= totalPages || listQuery.isFetching}
                onClick={() => setPage(page + 1)}
              >
                Next
                <ChevronRight className="size-3.5" aria-hidden />
              </Button>
            </div>
          </div>
        </>
      )}
    </div>
  )
}
