'use client'

import {
  AlertCircle,
  AlertTriangle,
  Ban,
  CheckCircle2,
  Clock,
  Loader2,
  PackageCheck,
  RefreshCw,
  ShieldCheck,
  Sparkles,
} from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import type { AssetVersionSummaryApi } from '@/lib/catalog/assets-api'
import { formatShortMonthDate } from '@/lib/format-date'
import { runQueryInBackground } from '@/lib/query/query-refresh'
import {
  useAssetProcessingJobsQuery,
  useAssetVersionProcessingJobsQuery,
} from '@/lib/seller/seller-processing-query'
import {
  isNonTerminalStatus,
  type AssetProcessingJobDto,
  type AssetProcessingJobStatus,
  type AssetProcessingJobType,
} from '@/lib/seller/seller-processing-schemas'
import { cn } from '@/lib/utils'

export interface AssetProcessingStatusPanelProps {
  assetId?: string
  assetVersionId?: string
  versions?: AssetVersionSummaryApi[]
  title?: string
  className?: string
}

function getJobTypeMeta(type: AssetProcessingJobType) {
  switch (type) {
    case 'ARCHIVE_INSPECTION':
      return {
        label: 'Archive Inspection',
        Icon: PackageCheck,
      }
    case 'MALWARE_SCAN':
      return {
        label: 'Malware & Security Scan',
        Icon: ShieldCheck,
      }
    case 'LISTING_COPILOT':
      return {
        label: 'AI Listing Analysis',
        Icon: Sparkles,
      }
  }
}

function getStatusBadge(
  status: AssetProcessingJobStatus,
  attemptCount: number,
  maxAttempts: number,
) {
  switch (status) {
    case 'QUEUED':
      return (
        <Badge variant="outline" className="border-border text-muted-foreground gap-1 text-[11px]">
          <Clock className="size-3" aria-hidden />
          Queued
        </Badge>
      )
    case 'RUNNING':
      return (
        <Badge
          variant="secondary"
          className="bg-sky-500/15 text-sky-400 border-sky-500/30 gap-1 text-[11px]"
        >
          <Loader2 className="size-3 animate-spin motion-reduce:animate-none" aria-hidden />
          Processing
        </Badge>
      )
    case 'RETRY_SCHEDULED':
      return (
        <Badge
          variant="secondary"
          className="bg-amber-500/15 text-amber-400 border-amber-500/30 gap-1 text-[11px]"
        >
          <RefreshCw className="size-3" aria-hidden />
          Retry Scheduled ({attemptCount}/{maxAttempts})
        </Badge>
      )
    case 'SUCCEEDED':
      return (
        <Badge
          variant="secondary"
          className="bg-emerald-500/15 text-emerald-400 border-emerald-500/30 gap-1 text-[11px]"
        >
          <CheckCircle2 className="size-3" aria-hidden />
          Passed
        </Badge>
      )
    case 'FAILED':
      return (
        <Badge variant="destructive" className="gap-1 text-[11px]">
          <AlertTriangle className="size-3" aria-hidden />
          Failed
        </Badge>
      )
    case 'CANCELLED':
      return (
        <Badge variant="outline" className="border-border text-muted-foreground gap-1 text-[11px]">
          <Ban className="size-3" aria-hidden />
          Cancelled
        </Badge>
      )
  }
}

function resolveVersionLabel(jobVersionId: string, versions?: AssetVersionSummaryApi[]): string {
  if (versions && versions.length > 0) {
    const match = versions.find((v) => v.id.toLowerCase() === jobVersionId.toLowerCase())
    if (match) {
      return `v${match.versionNumber}`
    }
  }
  const suffix = jobVersionId.length >= 6 ? jobVersionId.slice(-6) : jobVersionId
  return `Version …${suffix}`
}

export function AssetProcessingStatusPanel({
  assetId,
  assetVersionId,
  versions,
  title = 'Processing status',
  className,
}: AssetProcessingStatusPanelProps) {
  const isVersionQuery = Boolean(assetVersionId)
  const assetQuery = useAssetProcessingJobsQuery(assetId, {
    enabled: !isVersionQuery && Boolean(assetId),
  })
  const versionQuery = useAssetVersionProcessingJobsQuery(assetVersionId, {
    enabled: isVersionQuery,
  })

  const activeQuery = isVersionQuery ? versionQuery : assetQuery
  const { data: jobs, isPending, isError, error, refetch, isRefetching } = activeQuery

  if (isPending) {
    return (
      <div
        className={cn(
          'rounded-lg border border-border bg-card-elevated/20 p-4 space-y-3',
          className,
        )}
      >
        <div className="flex items-center justify-between">
          <Skeleton className="h-4 w-32 bg-muted-foreground/20" />
          <Skeleton className="h-4 w-16 bg-muted-foreground/20" />
        </div>
        <div className="space-y-2 pt-1">
          <Skeleton className="h-12 w-full rounded-md bg-muted-foreground/10" />
          <Skeleton className="h-12 w-full rounded-md bg-muted-foreground/10" />
        </div>
      </div>
    )
  }

  if (isError) {
    return (
      <div
        className={cn(
          'rounded-lg border border-destructive/30 bg-destructive/10 p-3.5 flex items-center justify-between gap-3 text-xs text-destructive',
          className,
        )}
        role="alert"
      >
        <div className="flex items-center gap-2 min-w-0">
          <AlertCircle className="size-4 shrink-0" aria-hidden />
          <span className="truncate">
            {error instanceof Error && !error.message.includes('ZodError')
              ? error.message
              : 'Could not load processing status.'}
          </span>
        </div>
        <Button
          type="button"
          variant="outline"
          size="sm"
          className="h-7 text-xs border-destructive/40 text-destructive hover:bg-destructive/10 shrink-0"
          onClick={() => runQueryInBackground(refetch())}
          disabled={isRefetching}
        >
          {isRefetching ? (
            <Loader2 className="size-3 animate-spin motion-reduce:animate-none mr-1" aria-hidden />
          ) : null}
          Retry
        </Button>
      </div>
    )
  }

  if (!jobs || jobs.length === 0) {
    return null
  }

  const hasActiveJobs = jobs.some((j) => isNonTerminalStatus(j.status))

  return (
    <div
      className={cn('rounded-lg border border-border bg-card-elevated/30 p-4 space-y-3', className)}
      aria-label="Asset processing status panel"
    >
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
            {title}
          </h4>
          {hasActiveJobs ? (
            <span
              className="inline-flex size-2 rounded-full bg-sky-400 animate-pulse motion-reduce:animate-none"
              title="Active processing in progress"
            />
          ) : null}
        </div>
        <span className="text-[11px] text-muted-foreground">
          {jobs.length} {jobs.length === 1 ? 'task' : 'tasks'}
        </span>
      </div>

      <ul className="space-y-2.5 divide-y divide-border/40">
        {jobs.map((job) => (
          <JobItem
            key={job.id}
            job={job}
            versionLabel={resolveVersionLabel(job.assetVersionId, versions)}
          />
        ))}
      </ul>
    </div>
  )
}

function JobItem({
  job,
  versionLabel,
}: {
  job: AssetProcessingJobDto
  versionLabel?: string | null
}) {
  const meta = getJobTypeMeta(job.type)
  const Icon = meta.Icon

  const status = job.status
  const stage = job.stage
  const attemptCount = job.attemptCount
  const maxAttempts = job.maxAttempts
  const errorSummary = job.errorSummary
  const updatedAt = job.updatedAt ?? job.createdAt

  return (
    <li className="pt-2.5 first:pt-0 space-y-1.5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <Icon className="size-4 text-muted-foreground shrink-0" aria-hidden />
          <span className="text-sm font-medium text-foreground">{meta.label}</span>
          {versionLabel ? (
            <Badge variant="outline" className="text-[10px] font-mono border-border px-1.5 py-0">
              {versionLabel}
            </Badge>
          ) : null}
          {stage && stage !== status ? (
            <span className="text-[10px] font-mono uppercase px-1.5 py-0.5 rounded bg-muted/60 text-muted-foreground">
              {stage}
            </span>
          ) : null}
        </div>
        <div className="flex items-center gap-2 shrink-0">
          {getStatusBadge(status, attemptCount, maxAttempts)}
        </div>
      </div>

      {errorSummary ? (
        <div className="rounded bg-destructive/10 border border-destructive/20 px-2.5 py-1.5 text-xs text-destructive flex items-start gap-2">
          <AlertCircle className="size-3.5 mt-0.5 shrink-0" aria-hidden />
          <span className="break-words leading-relaxed line-clamp-4">{errorSummary}</span>
        </div>
      ) : null}

      <div className="flex items-center justify-between text-[11px] text-muted-foreground">
        <span>
          {attemptCount > 1 ? (
            <>
              Attempt {attemptCount} of {maxAttempts}
            </>
          ) : null}
        </span>
        {updatedAt ? <span>Updated {formatShortMonthDate(updatedAt)}</span> : null}
      </div>
    </li>
  )
}
