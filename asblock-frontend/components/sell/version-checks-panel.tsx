'use client'

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { AlertCircle, CheckCircle2, Clock, Loader2, ShieldCheck, Upload } from 'lucide-react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import {
  fetchSellerAnalysisFindingsQuery,
  fetchSellerVersionReviewQuery,
  sellerKeys,
} from '@/lib/seller/seller-query'
import { retrySellerAnalysis } from '@/lib/seller/seller-draft-api'
import type { SellerAnalysisRetryRequest } from '@/lib/seller/seller-draft-schemas'
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
  const queryClient = useQueryClient()
  const [findingPageState, setFindingPageState] = useState({ reportId: '', page: 1 })
  const retryBody = useRef<SellerAnalysisRetryRequest | null>(null)
  const [retryAccepted, setRetryAccepted] = useState<'ACCEPTED' | 'RECHECK_QUEUED' | null>(null)
  const [retryConflictAt, setRetryConflictAt] = useState<number | null>(null)
  const reviewQuery = useQuery({
    queryKey: sellerKeys.versionReview(assetId, versionId),
    queryFn: () => fetchSellerVersionReviewQuery({ assetId, versionId }),
    refetchInterval: (query) => {
      const review = query.state.data
      const lifecycle = review?.analysisDetails?.lifecycle
      const upstreamActive =
        review?.processingStatus === 'PENDING_INSPECTION' ||
        review?.processingStatus === 'PENDING_MALWARE_SCAN'
      return upstreamActive || lifecycle === 'QUEUED' || lifecycle === 'RUNNING' ? 5000 : false
    },
  })
  const retryConflict = retryConflictAt !== null && reviewQuery.dataUpdatedAt <= retryConflictAt
  const analysis = reviewQuery.data?.analysisDetails
  const reportId = analysis?.reportId ?? ''
  const findingPage = findingPageState.reportId === reportId ? findingPageState.page : 1
  const findingsQuery = useQuery({
    queryKey: sellerKeys.analysisFindings(
      assetId,
      versionId,
      analysis?.reportId ?? 'none',
      findingPage,
    ),
    queryFn: ({ signal }) => {
      const reportId = analysis?.reportId
      if (!reportId) throw new Error('No admitted report is selected.')
      return fetchSellerAnalysisFindingsQuery({
        assetId,
        versionId,
        reportId,
        page: findingPage,
        signal,
      })
    },
    enabled: Boolean(analysis?.reportId && analysis.findingDetailsAvailable),
  })
  const findingsUnavailable =
    analysis?.findingDetailsAvailable === false || findingsQuery.data?.detailsAvailable === false

  useEffect(() => {
    if (!findingsUnavailable) return
    const queryKey = sellerKeys.analysisFindingsForVersion(assetId, versionId)
    void queryClient.cancelQueries({ queryKey }).then(() => {
      queryClient.removeQueries({ queryKey })
    })
  }, [assetId, findingsUnavailable, queryClient, versionId])
  const retryMutation = useMutation({
    mutationFn: (body: SellerAnalysisRetryRequest) => retrySellerAnalysis(assetId, versionId, body),
    onSuccess: (result) => {
      if (result.ok) {
        setRetryAccepted(result.value.status)
        setRetryConflictAt(null)
        retryBody.current = null
      } else if (result.code === 'ERR_ANALYSIS_RETRY_CONFLICT') {
        retryBody.current = null
        setRetryConflictAt(reviewQuery.dataUpdatedAt)
      }
      void queryClient
        .invalidateQueries({ queryKey: sellerKeys.versionReview(assetId, versionId) })
        .catch(() => undefined)
      void queryClient
        .invalidateQueries({ queryKey: sellerKeys.analysisFindingsForVersion(assetId, versionId) })
        .catch(() => undefined)
    },
  })

  if (reviewQuery.isPending) {
    return (
      <Skeleton
        className="h-24 w-full rounded-md bg-muted-foreground/20 animate-pulse"
        aria-busy="true"
      />
    )
  }

  if (reviewQuery.isError && !reviewQuery.data) {
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
  const canRetry = Boolean(analysis?.canRetry && analysis.retryExpectation)

  function retryAnalysis() {
    if (!analysis?.retryExpectation) return
    const expected = analysis.retryExpectation
    const prior = retryBody.current
    if (!prior) {
      setRetryAccepted(null)
      setRetryConflictAt(null)
      retryBody.current = {
        operationId: crypto.randomUUID(),
        expectedRunId: expected.runId,
        expectedWorkspaceRevision: expected.workspaceRevision,
        expectedPolicyDigest: expected.policyDigest,
        expectedInputDigest: expected.inputDigest,
      }
    }
    const body = retryBody.current
    if (body) retryMutation.mutate(body)
  }

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

      {reviewQuery.isError ? (
        <p className="text-xs text-destructive" role="status">
          Version checks may be out of date. Showing last loaded status; refresh to update.
        </p>
      ) : null}

      {analysis ? (
        <section
          className="space-y-3 border-t border-border pt-3"
          aria-labelledby={`analysis-title-${versionId}`}
        >
          <div className="flex flex-wrap items-center gap-2">
            <h5
              id={`analysis-title-${versionId}`}
              className="text-xs font-semibold text-foreground"
            >
              Code analysis
            </h5>
            <Badge variant="outline">{analysis.lifecycle.toLowerCase().replaceAll('_', ' ')}</Badge>
            {analysis.reportId && !analysis.reportIsCurrent ? (
              <Badge variant="secondary">Historical report — inputs changed</Badge>
            ) : null}
            {analysis.coverageLevel ? (
              <Badge variant="outline">{analysis.coverageLevel.toLowerCase()} coverage</Badge>
            ) : null}
          </div>
          <p className="text-xs text-muted-foreground">
            {analysis.analyzedFileCount} files analyzed · {analysis.omissionCount} omissions ·{' '}
            {analysis.findingCount} findings
          </p>
          <p className="text-xs text-muted-foreground">Scope: {analysis.comparisonScope}</p>
          {analysis.errorMessage ? (
            <p className="text-xs text-destructive" role="status">
              {analysis.errorMessage}
            </p>
          ) : null}
          <ul className="space-y-1 text-xs text-muted-foreground">
            {analysis.stages.map((stage) => (
              <li key={stage.stage}>
                {stage.stage}: {stage.outcome.toLowerCase()} ({stage.applicability.toLowerCase()})
              </li>
            ))}
          </ul>
          {analysis.reportId ? (
            <div className="space-y-2">
              {findingsUnavailable ? (
                <p className="text-xs text-muted-foreground" role="status">
                  Historical finding details are no longer available. Analysis summary and counts
                  remain.
                </p>
              ) : null}
              {!findingsUnavailable && findingsQuery.isPending ? (
                <Skeleton className="h-16 w-full" aria-busy="true" />
              ) : null}
              {!findingsUnavailable && findingsQuery.isError ? (
                <p role="alert" className="text-xs text-destructive">
                  Could not load findings. Refresh to try again.
                </p>
              ) : null}
              {!findingsUnavailable &&
                findingsQuery.data?.items.map((finding) => (
                  <article
                    key={finding.id}
                    className="rounded border border-border p-3 text-xs space-y-1.5"
                  >
                    <p className="font-medium text-foreground">
                      {finding.findingType.replaceAll('_', ' ')} · {finding.ownPath}
                    </p>
                    {finding.startLine ? (
                      <p className="text-muted-foreground">
                        Lines {finding.startLine}
                        {finding.endLine && finding.endLine !== finding.startLine
                          ? `–${finding.endLine}`
                          : ''}
                      </p>
                    ) : null}
                    <pre className="overflow-x-auto rounded bg-muted p-2 text-[11px] whitespace-pre-wrap break-words">
                      {finding.ownExcerpt}
                    </pre>
                    <p>{finding.reason}</p>
                    <p className="text-muted-foreground">Action: {finding.requestedAction}</p>
                    {finding.referenceKind === 'OWN_PRIOR_VERSION' ? (
                      <p className="text-muted-foreground">
                        Possible match in your earlier version.
                      </p>
                    ) : finding.referenceKind === 'FOREIGN_PRIVATE' ? (
                      <p className="text-muted-foreground">
                        Potential match against a private reference; source details are withheld.
                      </p>
                    ) : finding.referenceKind === 'FOREIGN_PUBLIC' ? (
                      <p className="text-muted-foreground">
                        Potential match against a public reference.
                      </p>
                    ) : null}
                    {finding.publicAttribution?.url ? (
                      <a
                        className="underline underline-offset-2"
                        href={finding.publicAttribution.url}
                        target="_blank"
                        rel="noreferrer"
                      >
                        {finding.publicAttribution.label}
                      </a>
                    ) : finding.publicAttribution ? (
                      <p className="text-muted-foreground">{finding.publicAttribution.label}</p>
                    ) : null}
                  </article>
                ))}
              {!findingsUnavailable && findingsQuery.data?.totalCount === 0 ? (
                <p className="text-xs text-muted-foreground" role="status">
                  No findings were recorded for this compared scope. Coverage and omissions above
                  still apply.
                </p>
              ) : null}
              {!findingsUnavailable && findingsQuery.data && findingsQuery.data.totalPages > 1 ? (
                <div className="flex items-center justify-between text-xs">
                  <button
                    type="button"
                    className="underline disabled:opacity-50"
                    disabled={findingPage <= 1}
                    onClick={() => setFindingPageState({ reportId, page: findingPage - 1 })}
                  >
                    Previous page
                  </button>
                  <span>
                    Page {findingPage} of {findingsQuery.data.totalPages}
                  </span>
                  <button
                    type="button"
                    className="underline disabled:opacity-50"
                    disabled={findingPage >= findingsQuery.data.totalPages}
                    onClick={() => setFindingPageState({ reportId, page: findingPage + 1 })}
                  >
                    Next page
                  </button>
                </div>
              ) : null}
            </div>
          ) : null}
          {analysis.limitations.map((limitation) => (
            <p key={limitation} className="text-xs text-muted-foreground">
              {limitation}
            </p>
          ))}
          {canRetry || retryAccepted || retryConflict ? (
            <div className="space-y-2">
              {canRetry ? (
                <button
                  type="button"
                  onClick={retryAnalysis}
                  disabled={retryMutation.isPending || retryConflict}
                  className="rounded border px-3 py-2 text-xs font-medium disabled:opacity-50"
                >
                  {retryMutation.isPending ? 'Queueing analysis…' : 'Retry analysis'}
                </button>
              ) : null}
              {retryAccepted ? (
                <p role="status" className="text-xs text-emerald-300">
                  {retryAccepted === 'RECHECK_QUEUED'
                    ? 'A scoped security proof check is queued before analysis can restart.'
                    : 'Retry accepted. Status will update when processing completes.'}
                </p>
              ) : null}
              {retryMutation.data && !retryMutation.data.ok ? (
                <p role="alert" className="text-xs text-destructive">
                  {retryMutation.data.message}{' '}
                  {retryMutation.data.code === 'ERR_ANALYSIS_RETRY_CONFLICT'
                    ? 'Refresh version review before trying again.'
                    : 'If response was uncertain, retry will replay the same operation.'}
                </p>
              ) : null}
              {retryMutation.isError ? (
                <p role="alert" className="text-xs text-destructive">
                  The result is unknown. Retry to safely replay the same request.
                </p>
              ) : null}
              {retryConflict ? (
                <p role="alert" className="text-xs text-destructive">
                  Retry expectations changed. Refresh version review and check the current
                  declaration before retrying.
                </p>
              ) : null}
            </div>
          ) : null}
          <p className="text-xs font-medium text-amber-700 dark:text-amber-300">
            Research support only. This analysis does not clear ownership, licensing, or
            publication.
          </p>
        </section>
      ) : null}

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
