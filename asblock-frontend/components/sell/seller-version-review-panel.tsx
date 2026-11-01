'use client'

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm, useWatch } from 'react-hook-form'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { Field, FieldError, FieldLabel } from '@/components/ui/field'
import { Badge } from '@/components/ui/badge'
import {
  fetchSellerResponses,
  requestSellerAppeal,
  withdrawSellerSubmission,
} from '@/lib/seller/seller-draft-api'
import {
  appealRationaleFormSchema,
  appealRequestSchema,
  type SellerVersionReview,
} from '@/lib/seller/seller-draft-schemas'
import { sellerKeys } from '@/lib/seller/seller-query'
import {
  versionReviewActionMessage,
  versionReviewBlockedReasonAction,
  versionReviewBlockedReasonMessage,
  versionReviewModerationLabel,
} from '@/lib/seller/version-review-status'
import { notificationsKeys } from '@/lib/notifications/notifications-query'

const MAX_APPEAL_RATIONALE = 2000

export function SellerVersionReviewPanel({
  assetId,
  versionId,
  review,
}: {
  assetId: string
  versionId: string
  review: SellerVersionReview
}) {
  const queryClient = useQueryClient()
  const [resultMessage, setResultMessage] = useState<{ text: string; error: boolean } | null>(null)
  const resultRef = useRef<HTMLParagraphElement>(null)
  const pendingRef = useRef(false)
  const retryPendingRef = useRef(false)
  const withdrawalPendingRef = useRef(false)
  const withdrawalAttemptRef = useRef<{
    key: string
    body: { expectedCaseRevision: number; operationId: string }
  } | null>(null)
  const attemptRef = useRef<{
    key: string
    body: ReturnType<typeof appealRequestSchema.parse>
  } | null>(null)
  const responseQuery = useQuery({
    queryKey: [...sellerKeys.responses(assetId, versionId), 1],
    queryFn: ({ signal }) => fetchSellerResponses(assetId, versionId, 1, signal),
    enabled: review.canAppeal,
    staleTime: 0,
  })
  const form = useForm({
    resolver: zodResolver(appealRationaleFormSchema),
    defaultValues: { rationale: '' },
  })
  const rationale = useWatch({ control: form.control, name: 'rationale' }) ?? ''
  const mutation = useMutation({
    mutationFn: async (values: { rationale: string }) => {
      if (pendingRef.current) throw new Error('An appeal request is already being sent.')
      pendingRef.current = true
      const latest = await responseQuery.refetch()
      const revisions = latest.data
      if (latest.isError || !revisions)
        throw new Error('Could not refresh review revisions. Try again.')
      const base = {
        workspaceId: revisions.workspaceId,
        expectedWorkspaceRevision: revisions.workspaceRevision,
        expectedCaseRevision: revisions.caseRevision,
        operationId: crypto.randomUUID(),
        rationale: values.rationale,
      }
      const key = JSON.stringify({ ...base, operationId: undefined })
      if (!attemptRef.current || (!retryPendingRef.current && attemptRef.current.key !== key)) {
        const parsed = appealRequestSchema.safeParse(base)
        if (!parsed.success) throw new Error('Enter an appeal reason within the allowed length.')
        attemptRef.current = { key, body: parsed.data }
      }
      const attempt = attemptRef.current
      if (!attempt) throw new Error('Could not prepare appeal request.')
      return requestSellerAppeal(assetId, versionId, attempt.body)
    },
    onSuccess: async (result) => {
      pendingRef.current = false
      if (result.ok) {
        retryPendingRef.current = false
        attemptRef.current = null
        form.reset({ rationale: '' })
        setResultMessage({ text: 'Appeal request submitted for independent review.', error: false })
      } else {
        retryPendingRef.current = Boolean(result.retryable)
        if (!retryPendingRef.current) attemptRef.current = null
        const code = result.blockedReasons?.[0]
        setResultMessage({
          text: code ? versionReviewActionMessage(code) : result.message,
          error: true,
        })
      }
      await invalidateSellerReview(queryClient, assetId, versionId)
    },
    onError: async (error) => {
      pendingRef.current = false
      retryPendingRef.current = true
      setResultMessage({
        text:
          error instanceof Error && error.message !== 'Network request failed.'
            ? error.message
            : 'The result is unknown. Retry this unchanged appeal request to safely replay it.',
        error: true,
      })
      await invalidateSellerReview(queryClient, assetId, versionId)
    },
  })
  const withdrawalMutation = useMutation({
    mutationFn: async (attempt: {
      submissionId: string
      body: { expectedCaseRevision: number; operationId: string }
    }) => {
      if (withdrawalPendingRef.current) {
        throw new Error('A withdrawal request is already being sent.')
      }
      withdrawalPendingRef.current = true
      return withdrawSellerSubmission(attempt.submissionId, attempt.body)
    },
    onSuccess: async (result) => {
      withdrawalPendingRef.current = false
      if (result.ok) {
        withdrawalAttemptRef.current = null
        setResultMessage({ text: 'Review request withdrawn.', error: false })
      } else {
        if (!result.retryable) withdrawalAttemptRef.current = null
        setResultMessage({ text: result.message, error: true })
      }
      await invalidateSellerReview(queryClient, assetId, versionId)
    },
    onError: async () => {
      withdrawalPendingRef.current = false
      setResultMessage({
        text: 'The result is unknown. Retry this withdrawal to safely replay it.',
        error: true,
      })
      await invalidateSellerReview(queryClient, assetId, versionId)
    },
  })

  useEffect(() => {
    if (resultMessage) resultRef.current?.focus()
  }, [resultMessage])

  const decision = review.decision
  const displaysDecisionReason =
    decision?.outcome === 'CHANGES_REQUESTED' || decision?.outcome === 'REJECTED'
  const verifierDisabled = review.analysisDetails?.stages.some(
    (stage) => stage.stage === 'VERIFIER' && stage.errorCode === 'POLICY_DISABLED',
  )
  const publicationLabel =
    decision?.publication === 'PUBLISHED' || (!decision && review.publicationEligible)
      ? 'Published'
      : decision?.publication === 'HISTORICAL'
        ? 'Approved, historical version'
        : 'Not published'
  const submitAttempt = form.handleSubmit((values) => {
    setResultMessage(null)
    mutation.mutate(values)
  })
  const retryAttempt = () => void submitAttempt()
  const submitWithdrawal = () => {
    const active = review.activeSubmission
    if (!active || withdrawalPendingRef.current) return
    const key = `${active.submissionId}:${active.caseRevision}`
    if (!withdrawalAttemptRef.current || withdrawalAttemptRef.current.key !== key) {
      withdrawalAttemptRef.current = {
        key,
        body: { expectedCaseRevision: active.caseRevision, operationId: crypto.randomUUID() },
      }
    }
    const attempt = withdrawalAttemptRef.current
    if (attempt) {
      withdrawalMutation.mutate({ submissionId: active.submissionId, body: attempt.body })
    }
  }
  const retryWithdrawal = () => submitWithdrawal()
  const uncertain =
    mutation.isError || Boolean(mutation.data && !mutation.data.ok && mutation.data.retryable)
  const withdrawalUncertain =
    withdrawalMutation.isError ||
    Boolean(
      withdrawalMutation.data && !withdrawalMutation.data.ok && withdrawalMutation.data.retryable,
    )

  return (
    <section aria-label="Review status" className="space-y-3 rounded-md border border-border p-3">
      <div className="flex flex-wrap items-center gap-2">
        <h4 className="text-sm font-semibold">Review status</h4>
        <Badge variant="outline">{versionReviewModerationLabel(review.moderationState)}</Badge>
        <span className="text-xs text-muted-foreground">Publication: {publicationLabel}</span>
      </div>

      {decision ? (
        <div className="space-y-1 text-xs">
          <p>
            Decision: <span className="font-medium">{decision.outcome.replaceAll('_', ' ')}</span>
          </p>
          <p>
            Decided{' '}
            <time dateTime={decision.decidedAt}>
              {new Date(decision.decidedAt).toLocaleString()}
            </time>
          </p>
          {displaysDecisionReason && decision.reason ? (
            <p className="whitespace-pre-wrap break-words" aria-label="Decision reason">
              {decision.reason}
            </p>
          ) : null}
        </div>
      ) : null}

      {verifierDisabled ? (
        <p className="text-xs text-muted-foreground" role="note">
          Pair verification was disabled by the fixed evaluation policy. Retained findings and
          available retrieval evidence receive human review; this does not mean verification
          succeeded.
        </p>
      ) : null}

      {review.blockedReasons.length > 0 ? (
        <div aria-label="Review blockers" className="space-y-1 text-xs">
          <p className="font-medium">Submission is not available yet.</p>
          <ul className="list-disc space-y-1 pl-5">
            {review.blockedReasons.map((code) => (
              <li key={code}>
                {versionReviewBlockedReasonMessage(code)}
                {versionReviewBlockedReasonAction(code)
                  ? ` ${versionReviewBlockedReasonAction(code)}`
                  : ''}
              </li>
            ))}
          </ul>
        </div>
      ) : null}
      {review.canSubmit ? (
        <p role="status" className="text-xs">
          This version is eligible for submission for human review.
        </p>
      ) : null}

      {review.appeal ? (
        <div className="space-y-1 text-xs" aria-label="Appeal status">
          <p>Appeal status: {review.appeal.state.replaceAll('_', ' ')}</p>
          <p>
            Requested{' '}
            <time dateTime={review.appeal.requestedAt}>
              {new Date(review.appeal.requestedAt).toLocaleString()}
            </time>
          </p>
          {review.appeal.decidedAt ? (
            <p>
              Decided{' '}
              <time dateTime={review.appeal.decidedAt}>
                {new Date(review.appeal.decidedAt).toLocaleString()}
              </time>
            </p>
          ) : null}
        </div>
      ) : null}

      {review.history.length > 0 ? (
        <ol
          aria-label="Review history"
          className="list-decimal space-y-2 border-t border-border pt-3 pl-5 text-xs"
        >
          {review.history.map((entry) => (
            <li key={entry.submissionId} className="space-y-1">
              <p>
                {entry.kind === 'APPEAL' ? 'Appeal' : 'Review request'}:{' '}
                {formatSubmissionState(entry.state)}
              </p>
              <p>
                Submitted{' '}
                <time dateTime={entry.createdAt}>{new Date(entry.createdAt).toLocaleString()}</time>
              </p>
              {entry.decisionOutcome ? (
                <p>
                  Decision: {formatSubmissionState(entry.decisionOutcome)}
                  {entry.decidedAt ? (
                    <>
                      {' '}
                      on{' '}
                      <time dateTime={entry.decidedAt}>
                        {new Date(entry.decidedAt).toLocaleString()}
                      </time>
                    </>
                  ) : null}
                </p>
              ) : null}
              {entry.decisionReason ? (
                <p className="whitespace-pre-wrap break-words" aria-label="Decision reason">
                  {entry.decisionReason}
                </p>
              ) : null}
            </li>
          ))}
        </ol>
      ) : null}

      {review.activeSubmission ? (
        <div className="border-t border-border pt-3">
          {withdrawalUncertain ? (
            <Button type="button" disabled={withdrawalMutation.isPending} onClick={retryWithdrawal}>
              Retry withdrawal
            </Button>
          ) : (
            <Button
              type="button"
              variant="outline"
              disabled={withdrawalMutation.isPending}
              onClick={submitWithdrawal}
            >
              {withdrawalMutation.isPending
                ? 'Withdrawing review request…'
                : review.activeSubmission.kind === 'APPEAL'
                  ? 'Withdraw appeal'
                  : 'Withdraw review request'}
            </Button>
          )}
        </div>
      ) : null}

      {review.canAppeal ? (
        <form className="space-y-2 border-t border-border pt-3" onSubmit={submitAttempt}>
          <h5 className="text-xs font-semibold">Request another review</h5>
          <p className="text-xs text-muted-foreground">
            Explain why an independent Moderator should review this rejection. A substantive change
            to the reviewed material needs a new review request instead.
          </p>
          {responseQuery.isPending ? <p role="status">Loading current review revisions…</p> : null}
          {responseQuery.isError ? (
            <p role="alert">Could not load current review revisions. Refresh and try again.</p>
          ) : null}
          <Field data-invalid={Boolean(form.formState.errors.rationale)}>
            <FieldLabel htmlFor={`appeal-rationale-${versionId}`}>Appeal rationale</FieldLabel>
            <Textarea
              id={`appeal-rationale-${versionId}`}
              aria-invalid={Boolean(form.formState.errors.rationale)}
              aria-describedby={`appeal-rationale-count-${versionId}`}
              maxLength={4000}
              disabled={mutation.isPending || uncertain}
              {...form.register('rationale')}
            />
            <FieldError
              errors={
                form.formState.errors.rationale ? [form.formState.errors.rationale] : undefined
              }
            />
          </Field>
          <p id={`appeal-rationale-count-${versionId}`} className="text-xs text-muted-foreground">
            {rationale.trim().length} / {MAX_APPEAL_RATIONALE} characters after trimming
          </p>
          {uncertain ? (
            <Button type="button" disabled={mutation.isPending} onClick={retryAttempt}>
              Retry appeal request
            </Button>
          ) : (
            <Button
              type="submit"
              disabled={
                mutation.isPending ||
                responseQuery.isPending ||
                responseQuery.isError ||
                !rationale.trim()
              }
            >
              {mutation.isPending ? 'Requesting review…' : 'Request another review'}
            </Button>
          )}
        </form>
      ) : null}
      {resultMessage ? (
        <p ref={resultRef} tabIndex={-1} role={resultMessage.error ? 'alert' : 'status'}>
          {resultMessage.text}
        </p>
      ) : null}
    </section>
  )
}

async function invalidateSellerReview(
  queryClient: ReturnType<typeof useQueryClient>,
  assetId: string,
  versionId: string,
) {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: sellerKeys.versionReview(assetId, versionId) }),
    queryClient.invalidateQueries({ queryKey: sellerKeys.responses(assetId, versionId) }),
    queryClient.invalidateQueries({ queryKey: sellerKeys.versions(assetId) }),
    queryClient.invalidateQueries({ queryKey: notificationsKeys.all }),
  ])
}

function formatSubmissionState(state: string): string {
  const words = state.toLowerCase().replaceAll('_', ' ')
  return words.charAt(0).toUpperCase() + words.slice(1)
}
