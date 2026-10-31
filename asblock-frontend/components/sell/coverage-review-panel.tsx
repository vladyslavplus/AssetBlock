'use client'

import { useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { ApiRequestError } from '@/lib/http/api-client'
import { fetchSellerResponses, requestSellerCoverageReview } from '@/lib/seller/seller-draft-api'
import { sellerKeys } from '@/lib/seller/seller-query'
import {
  coverageReviewCreateRequestSchema,
  type CoverageReviewCreateRequest,
  type SellerVersionReview,
} from '@/lib/seller/seller-draft-schemas'
import { versionReviewBlockedReasonMessage } from '@/lib/seller/version-review-status'

export function CoverageReviewPanel({
  assetId,
  versionId,
  review,
}: {
  assetId: string
  versionId: string
  review: SellerVersionReview
}) {
  const client = useQueryClient()
  const status = review.coverageReview
  const reportId = review.analysisDetails?.reportId
  const query = useQuery({
    queryKey: [...sellerKeys.responses(assetId, versionId), 1],
    queryFn: ({ signal }) => fetchSellerResponses(assetId, versionId, 1, signal),
    enabled: Boolean(status?.canRequest),
  })
  const body = useRef<CoverageReviewCreateRequest | null>(null)
  const mutation = useMutation({
    mutationFn: () => {
      if (!query.data || !reportId || query.data.reportId !== reportId)
        throw new Error('Refresh the current report before requesting coverage review.')
      body.current ??= coverageReviewCreateRequestSchema.parse({
        workspaceId: query.data.workspaceId,
        reportId,
        expectedWorkspaceRevision: query.data.workspaceRevision,
        operationId: crypto.randomUUID(),
      })
      return requestSellerCoverageReview(assetId, versionId, body.current)
    },
    onSuccess: async (result) => {
      if (result.ok || !result.retryable) body.current = null
      if (result.ok) toast.success('Coverage review requested.')
      else toast.error(result.message)
      await Promise.all([
        client.invalidateQueries({ queryKey: sellerKeys.versionReview(assetId, versionId) }),
        client.invalidateQueries({ queryKey: sellerKeys.responses(assetId, versionId) }),
      ])
    },
  })
  const uncertain =
    (mutation.error instanceof ApiRequestError && mutation.error.status === 0) ||
    Boolean(mutation.data && !mutation.data.ok && mutation.data.retryable)
  if (
    !status ||
    !review.analysisDetails?.reportIsCurrent ||
    !['PARTIAL', 'UNSUPPORTED'].includes(review.analysisDetails.coverageLevel ?? '')
  )
    return null
  return (
    <section className="space-y-2 border-t pt-3 text-xs" aria-label="Coverage review">
      <p>Coverage review: {status.status.toLowerCase().replaceAll('_', ' ')}.</p>
      <p>
        Incomplete automatic coverage requires a competent Moderator to assess a readable manual
        review scope. An accepted assessment permits submission; it does not approve publication.
      </p>
      {status.reasonCode ? <p>{versionReviewBlockedReasonMessage(status.reasonCode)}</p> : null}
      {status.canRequest || uncertain ? (
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={mutation.isPending || query.isPending}
          onClick={() => mutation.mutate()}
        >
          {mutation.isPending
            ? 'Requesting review...'
            : uncertain
              ? 'Retry same coverage request'
              : 'Request coverage review'}
        </Button>
      ) : null}
      {query.isError ? <p role="alert">{query.error.message}</p> : null}
      {mutation.isError ? (
        <p role="alert">
          {uncertain
            ? 'Result unknown. Retry safely replays the same coverage request.'
            : mutation.error.message}
        </p>
      ) : null}
      {mutation.data ? (
        <p role={mutation.data.ok ? 'status' : 'alert'}>
          {mutation.data.ok ? 'Coverage review requested.' : mutation.data.message}
        </p>
      ) : null}
    </section>
  )
}
