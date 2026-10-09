'use client'

import { useQuery } from '@tanstack/react-query'
import { AlertCircle, CheckCircle2, Clock, Loader2, ShieldCheck, Upload } from 'lucide-react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import { fetchSellerVersionReviewQuery, sellerKeys } from '@/lib/seller/seller-query'
import {
  isVersionPublished,
  versionReviewBlockedReasonAction,
  versionReviewBlockedReasonMessage,
  versionReviewModerationLabel,
} from '@/lib/seller/version-review-status'

const PROCESSING_LABELS: Record<string, string> = {
  PENDING_INSPECTION: 'Inspecting archive',
  PENDING_MALWARE_SCAN: 'Scanning for malware',
  READY: 'File checks passed',
  REJECTED: 'Security checks failed',
  PROCESSING_FAILED: 'Processing failed',
}

interface VersionChecksPanelProps {
  assetId: string
  versionId: string
}

export function VersionChecksPanel({ assetId, versionId }: VersionChecksPanelProps) {
  const reviewQuery = useQuery({
    queryKey: sellerKeys.versionReview(assetId, versionId),
    queryFn: () => fetchSellerVersionReviewQuery({ assetId, versionId }),
  })

  if (reviewQuery.isPending) {
    return (
      <Skeleton
        className="h-24 w-full rounded-md bg-muted-foreground/20 animate-pulse"
        aria-busy="true"
      />
    )
  }

  if (reviewQuery.isError) {
    return (
      <p className="text-sm text-destructive" role="alert">
        {reviewQuery.error instanceof Error
          ? reviewQuery.error.message
          : 'Could not load version checks.'}
      </p>
    )
  }

  const review = reviewQuery.data
  const processingLabel = PROCESSING_LABELS[review.processingStatus] ?? review.processingStatus
  const published = isVersionPublished(review)

  return (
    <div
      className="rounded-md border border-border p-3 space-y-3"
      data-testid={`version-checks-${versionId}`}
    >
      <div className="flex flex-wrap items-center gap-2">
        <h4 className="text-xs font-semibold text-foreground">Version checks</h4>
        <span className="text-[11px] text-muted-foreground">Version {review.versionNumber}</span>
        {published ? (
          <Badge
            variant="outline"
            className="border-emerald-500/40 bg-emerald-500/10 text-[11px] text-emerald-200"
          >
            <CheckCircle2 className="mr-1 size-3" aria-hidden />
            Publicly listed
          </Badge>
        ) : (
          <Badge variant="outline" className="border-border text-[11px] text-muted-foreground">
            Candidate — not published
          </Badge>
        )}
      </div>

      <ul className="space-y-1.5 text-xs text-muted-foreground">
        <li className="flex items-center gap-2">
          <Upload className="size-3.5 shrink-0" aria-hidden />
          <span>
            <span className="text-foreground/90">File checks:</span> {processingLabel}
            {review.processingErrorSummary ? ` — ${review.processingErrorSummary}` : ''}
          </span>
        </li>
        <li className="flex items-center gap-2">
          <Clock className="size-3.5 shrink-0" aria-hidden />
          <span>
            <span className="text-foreground/90">Analysis:</span>{' '}
            {review.analysis === 'ANALYSIS_AVAILABLE'
              ? 'Available'
              : 'Code analysis is not available yet'}
          </span>
        </li>
        <li className="flex items-center gap-2">
          <ShieldCheck className="size-3.5 shrink-0" aria-hidden />
          <span>
            <span className="text-foreground/90">Moderation:</span>{' '}
            {versionReviewModerationLabel(review.moderationState)}
          </span>
        </li>
        <li className="flex items-center gap-2">
          <CheckCircle2 className="size-3.5 shrink-0" aria-hidden />
          <span>
            <span className="text-foreground/90">Publication:</span>{' '}
            {published
              ? 'Approved and publicly listed.'
              : 'Not published. Publication needs an approved review — passing file checks alone is not enough.'}
          </span>
        </li>
      </ul>

      {!review.canSubmit ? (
        <Alert className="border-amber-500/40 bg-amber-500/10 py-3">
          <AlertCircle className="h-4 w-4 text-amber-600 dark:text-amber-400" />
          <AlertDescription className="text-xs text-amber-800 dark:text-amber-200 space-y-1.5">
            <p className="font-medium text-foreground/90">Submission is not available yet.</p>
            <ul className="space-y-1">
              {review.blockedReasons.map((reason) => (
                <li key={reason}>
                  {versionReviewBlockedReasonMessage(reason)}
                  {versionReviewBlockedReasonAction(reason)
                    ? ` ${versionReviewBlockedReasonAction(reason)}`
                    : ''}
                </li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      ) : (
        <p className="flex items-center gap-1.5 text-xs text-emerald-300">
          <CheckCircle2 className="size-3.5" aria-hidden />
          This version is eligible for submission.
        </p>
      )}
    </div>
  )
}

export function VersionChecksPanelSkeleton() {
  return (
    <div className="flex items-center gap-2 text-sm text-muted-foreground py-2">
      <Loader2 className="size-4 animate-spin" aria-hidden />
      Loading checks…
    </div>
  )
}
