'use client'

import { useEffect, useRef, useState } from 'react'
import Link from 'next/link'
import { useAuth } from '@/components/auth/auth-context'
import { Button } from '@/components/ui/button'
import {
  ModerationApiError,
  moderationErrorMessage,
  useAssessCoverage,
  useChangeModerationClaim,
  useCoverageRequests,
  useModerationCase,
  useModerationQueue,
  useSubmitModerationDecision,
  fetchModerationEvidence,
} from '@/lib/moderation/moderation-query'
import type {
  ModerationDecisionRequest,
  RestrictedEvidenceKind,
} from '@/lib/moderation/moderation-schemas'

const previewKinds: RestrictedEvidenceKind[] = [
  'DECLARATION',
  'MATERIAL_METADATA',
  'SELLER_EVIDENCE',
  'COVERAGE_INVENTORY',
  'ARCHIVE_README',
]

function decisionBlockerMessage(code: string) {
  const messages: Record<string, string> = {
    ERR_MODERATION_APPEAL_DECISION_NOT_ALLOWED: 'This decision is not allowed for an appeal case.',
    ERR_VERSION_FILE_CHECKS_INCOMPLETE:
      'This version does not have current security and processing checks.',
    ERR_REVIEW_COVERAGE_REQUIRED: 'An accepted coverage assessment is required.',
    ERR_ANALYSIS_REPORT_NOT_TRUSTED: 'The persisted analysis report failed its admission checks.',
    ERR_MODERATION_WORKSPACE_STALE: 'The submission workspace changed after analysis.',
    ERR_DECLARATION_INCOMPLETE: 'The frozen source declaration is incomplete.',
    ERR_COVERAGE_ASSESSMENT_INVALIDATED: 'The coverage assessment is no longer current.',
    ERR_COVERAGE_ASSESSOR_UNAVAILABLE: 'Coverage review needs a new eligible reviewer.',
    ERR_COVERAGE_ASSESSMENT_BLOCKED:
      'The accepted coverage assessment no longer permits this decision.',
    ERR_REVIEW_PLAN_REFERENCE_MISMATCH:
      'Confirm the exact completed plan reference from the assessment.',
    ERR_ANALYSIS_NOT_AVAILABLE: 'Required analysis or response status is unavailable.',
    ERR_MODERATION_DECISION_RESPONSES_OUTSTANDING:
      'Seller responses to required findings are outstanding.',
    ERR_MODERATION_DISPOSITIONS_INVALID:
      'Provide one valid disposition for every material finding.',
    ERR_MODERATION_DECISION_BLOCKED: 'One or more dispositions prevent approval.',
    ERR_PUBLICATION_METADATA_UNAVAILABLE: 'Required publication metadata is unavailable.',
    ERR_MODERATION_CONCURRENT_UPDATE:
      'The case changed during the decision. Review its refreshed state.',
  }
  return (
    messages[code] ??
    'The backend blocked this decision. Review the refreshed case before retrying.'
  )
}

function EvidencePreview({
  submissionId,
  kind,
  findingId,
  close,
}: {
  submissionId: string
  kind: RestrictedEvidenceKind
  findingId?: string
  close: () => void
}) {
  const [result, setResult] = useState<Awaited<ReturnType<typeof fetchModerationEvidence>> | null>(
    null,
  )
  const [error, setError] = useState<string | null>(null)
  const controller = useRef<AbortController | null>(null)
  const evidenceRef = useRef<Awaited<ReturnType<typeof fetchModerationEvidence>> | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    headingRef.current?.focus()
    evidenceRef.current = null
    const active = new AbortController()
    controller.current = active
    void Promise.resolve()
      .then(() => {
        if (active.signal.aborted) return null
        setResult(null)
        setError(null)
        return fetchModerationEvidence(submissionId, kind, findingId, active.signal)
      })
      .then((value) => {
        if (!value || active.signal.aborted) return
        evidenceRef.current = value
        setResult(value)
      })
      .catch((reason: unknown) => {
        if (!active.signal.aborted)
          setError(moderationErrorMessage(reason instanceof Error ? reason : null))
      })
    return () => {
      active.abort()
      controller.current = null
      evidenceRef.current = null
    }
  }, [findingId, kind, submissionId])

  return (
    <section aria-labelledby="evidence-preview-heading" className="space-y-2 rounded-md border p-4">
      <div className="flex items-center justify-between gap-2">
        <h3 ref={headingRef} tabIndex={-1} id="evidence-preview-heading" className="font-semibold">
          {kind.replaceAll('_', ' ')}
        </h3>
        <Button type="button" variant="outline" onClick={close}>
          Close preview
        </Button>
      </div>
      {error ? <p role="alert">{error}</p> : null}
      {!error && !result ? <p role="status">Loading evidence preview…</p> : null}
      {result ? (
        <div>
          <p className="text-sm">
            {result.itemCount} items{result.truncated ? '; preview truncated' : ''}
          </p>
          <pre className="max-h-96 overflow-auto whitespace-pre-wrap break-words rounded bg-muted p-3 text-sm">
            {result.content}
          </pre>
        </div>
      ) : null}
    </section>
  )
}

export function ModerationCaseWorkspace({ submissionId }: { submissionId: string }) {
  const { user } = useAuth()
  const caseQuery = useModerationCase(submissionId)
  const queue = useModerationQueue({ pageSize: 100 })
  const coverage = useCoverageRequests()
  const claimMutation = useChangeModerationClaim()
  const assessMutation = useAssessCoverage()
  const decisionMutation = useSubmitModerationDecision()
  const [preview, setPreview] = useState<{
    kind: RestrictedEvidenceKind
    findingId?: string
  } | null>(null)
  const [outcome, setOutcome] = useState<'ACCEPTED' | 'BLOCKED'>('BLOCKED')
  const [competence, setCompetence] = useState('Asset structure review')
  const [scopePath, setScopePath] = useState('')
  const [scopeLanguage, setScopeLanguage] = useState('')
  const [scopeRole, setScopeRole] = useState('')
  const [scopeReadable, setScopeReadable] = useState(false)
  const [manageableScope, setManageableScope] = useState(false)
  const [reviewPlan, setReviewPlan] = useState('')
  const [completedPlanReference, setCompletedPlanReference] = useState('')
  const [rationale, setRationale] = useState('')
  const [decisionOutcomes, setDecisionOutcomes] = useState<
    Record<'STANDARD' | 'APPEAL', 'APPROVE' | 'REQUEST_CHANGES' | 'REJECT' | ''>
  >({ STANDARD: 'APPROVE', APPEAL: '' })
  const [decisionRationale, setDecisionRationale] = useState('')
  const [manualPlanReference, setManualPlanReference] = useState<string | null>(null)
  const [dispositions, setDispositions] = useState<
    Record<string, { disposition: string; rationale: string }>
  >({})
  const [decisionAttempt, setDecisionAttempt] = useState<{
    fingerprint: string
    operationId: string
  } | null>(null)
  const [attempt, setAttempt] = useState<{ fingerprint: string; operationId: string } | null>(null)
  const [actionNotice, setActionNotice] = useState('')
  const liveRegion = useRef<HTMLParagraphElement>(null)
  const previewTrigger = useRef<HTMLButtonElement | null>(null)

  useEffect(() => {
    if (preview === null) previewTrigger.current?.focus()
  }, [preview])

  const summary = caseQuery.data
  const decisionOutcome = summary ? decisionOutcomes[summary.kind] : ''
  const setDecisionOutcome = (value: 'APPROVE' | 'REQUEST_CHANGES' | 'REJECT' | '') => {
    const caseKind = summary?.kind ?? 'STANDARD'
    setDecisionOutcomes((current) => ({ ...current, [caseKind]: value }))
  }
  const queueItem = queue.data?.pages
    .flatMap((page) => page.items)
    .find((item) => item.submissionId === submissionId)
  const coverageItems = coverage.data?.pages.flatMap((page) => page.items) ?? []
  const coverageMatches = summary?.retrievalEvidence?.reportId
    ? coverageItems.filter(
        (item) =>
          item.assetVersionId === summary.assetVersionId &&
          item.reportId === summary.retrievalEvidence?.reportId,
      )
    : []
  const coverageRequest =
    coverageMatches.length === 1 && !coverage.hasNextPage ? coverageMatches[0] : undefined

  async function changeClaim(action: 'claim' | 'release') {
    if (!summary || claimMutation.isPending) return
    claimMutation.reset()
    try {
      await claimMutation.mutateAsync({
        submissionId,
        action,
        body: { operationId: crypto.randomUUID(), expectedCaseRevision: summary.caseRevision },
      })
      await Promise.all([caseQuery.refetch(), queue.refetch()])
      setActionNotice('Claim state updated.')
      liveRegion.current?.focus()
    } catch (error) {
      if (error instanceof ModerationApiError && error.status === 409) {
        await Promise.all([caseQuery.refetch(), queue.refetch()])
        setActionNotice('Case refreshed. Review the current claim state.')
        liveRegion.current?.focus()
      }
    }
  }

  async function submitCoverage(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!coverageRequest) return
    const scope =
      outcome === 'ACCEPTED'
        ? [
            {
              path: scopePath,
              language: scopeLanguage,
              roleInCoreLogic: scopeRole,
              readable: scopeReadable,
              evidenceKind: 'COVERAGE_INVENTORY' as const,
            },
          ]
        : []
    const payload = {
      expectedRequestRevision: coverageRequest.requestRevision,
      expectedTupleDigest: coverageRequest.tupleDigest,
      outcome,
      competence: competence
        .split(',')
        .map((item) => item.trim())
        .filter(Boolean),
      scope,
      manageableScope,
      reviewPlan,
      completedPlanReference: outcome === 'ACCEPTED' ? completedPlanReference : '',
      rationale,
    }
    const fingerprint = JSON.stringify(payload)
    const operationId =
      attempt?.fingerprint === fingerprint ? attempt.operationId : crypto.randomUUID()
    setAttempt({ fingerprint, operationId })
    assessMutation.reset()
    try {
      await assessMutation.mutateAsync({
        requestId: coverageRequest.requestId,
        body: { operationId, ...payload },
      })
      setRationale('')
      setAttempt(null)
      setActionNotice('Coverage assessment saved.')
      liveRegion.current?.focus()
    } catch (error) {
      if (error instanceof ModerationApiError && error.status === 409) {
        await Promise.all([caseQuery.refetch(), coverage.refetch()])
        setActionNotice('Case and coverage request refreshed. Your assessment text was preserved.')
        liveRegion.current?.focus()
      }
    }
  }

  async function submitDecision(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!summary || !ownClaim) return
    if (!decisionOutcome) {
      setActionNotice('Choose a decision outcome before submitting.')
      liveRegion.current?.focus()
      return
    }
    const requiredFindings =
      summary.retrievalEvidence?.candidates.filter((candidate) => candidate.responseRequired) ?? []
    if (
      requiredFindings.some(
        (candidate) =>
          !dispositions[candidate.findingId]?.disposition ||
          !dispositions[candidate.findingId]?.rationale.trim(),
      )
    ) {
      setActionNotice('Choose a disposition and provide a rationale for every material finding.')
      liveRegion.current?.focus()
      return
    }
    if (requiredFindings.some((candidate) => !candidate.answered)) {
      setActionNotice(
        'Required seller responses are still outstanding. The case cannot be decided yet.',
      )
      liveRegion.current?.focus()
      return
    }
    if (
      summary.manualPlanCompletionReferenceRequired &&
      (!manualPlanReference?.trim() ||
        manualPlanReference.trim().length > 200 ||
        manualPlanReference.includes('\0'))
    ) {
      setActionNotice('Enter the completed plan reference required by the accepted assessment.')
      liveRegion.current?.focus()
      return
    }
    const decisionDispositions = requiredFindings.flatMap((candidate) => {
      const selected = dispositions[candidate.findingId]
      return selected
        ? [
            {
              findingId: candidate.findingId,
              disposition:
                selected.disposition as ModerationDecisionRequest['dispositions'][number]['disposition'],
              rationale: selected.rationale,
            },
          ]
        : []
    })
    const payload: Omit<ModerationDecisionRequest, 'operationId'> = {
      basis: summary.basis,
      outcome: decisionOutcome,
      rationale: decisionRationale,
      dispositions: decisionDispositions,
      manualPlanCompletionReference: summary.manualPlanCompletionReferenceRequired
        ? manualPlanReference
        : null,
    }
    const fingerprint = JSON.stringify(payload)
    const operationId =
      decisionAttempt?.fingerprint === fingerprint
        ? decisionAttempt.operationId
        : crypto.randomUUID()
    setDecisionAttempt({ fingerprint, operationId })
    decisionMutation.reset()
    try {
      await decisionMutation.mutateAsync({ submissionId, body: { operationId, ...payload } })
      setDecisionAttempt(null)
      setActionNotice('Decision saved.')
      liveRegion.current?.focus()
      await Promise.all([caseQuery.refetch(), queue.refetch()])
    } catch (error) {
      if (error instanceof ModerationApiError && error.status === 409) {
        await Promise.all([caseQuery.refetch(), queue.refetch()])
        setActionNotice(
          'The case was refreshed. Your decision and disposition text remains in the form.',
        )
        liveRegion.current?.focus()
      }
    }
  }

  if (caseQuery.isPending) return <p role="status">Loading case…</p>
  if (caseQuery.isError)
    return (
      <section>
        <h2>Could not load case</h2>
        <p role="alert">{moderationErrorMessage(caseQuery.error)}</p>
        <Button onClick={() => void caseQuery.refetch()}>Try again</Button>
      </section>
    )
  if (!summary) return <p role="alert">Case response was empty.</p>

  const claimError = moderationErrorMessage(claimMutation.error, claimMutation.variables?.action)
  const coverageError = moderationErrorMessage(assessMutation.error)
  const candidates = summary.retrievalEvidence?.candidates ?? []
  const ownClaim = Boolean(user?.id && summary.isClaimedByCaller)
  return (
    <main className="space-y-6" aria-labelledby="case-workspace-heading">
      <Link className="text-sm underline" href="/moderation">
        Back to queue
      </Link>
      <header className="space-y-2">
        <h2 id="case-workspace-heading" className="text-xl font-semibold">
          Moderation case
        </h2>
        <dl className="grid gap-2 sm:grid-cols-2">
          <div>
            <dt className="font-medium">Asset ID</dt>
            <dd className="break-all">{summary.assetId}</dd>
          </div>
          <div>
            <dt className="font-medium">Version ID</dt>
            <dd className="break-all">{summary.assetVersionId}</dd>
          </div>
          <div>
            <dt className="font-medium">Submission ID</dt>
            <dd className="break-all">{summary.submissionId}</dd>
          </div>
          <div>
            <dt className="font-medium">State</dt>
            <dd>{summary.state.replaceAll('_', ' ')}</dd>
          </div>
          <div>
            <dt className="font-medium">Submission kind</dt>
            <dd>{summary.kind === 'APPEAL' ? 'Appeal' : 'Standard'}</dd>
          </div>
          <div>
            <dt className="font-medium">Claim holder</dt>
            <dd>
              {queueItem
                ? queueItem.claimHolderId
                  ? ownClaim
                    ? 'You'
                    : (queueItem.claimHolderName ?? 'Another reviewer')
                  : 'Unclaimed'
                : 'Not included in case response'}
            </dd>
          </div>
        </dl>
        {queueItem ? (
          <div className="flex gap-2">
            {queueItem.claimHolderId === null || queueItem.claimHolderId === undefined ? (
              <Button
                type="button"
                disabled={claimMutation.isPending}
                onClick={() => void changeClaim('claim')}
              >
                Claim case
              </Button>
            ) : null}
            {ownClaim ? (
              <Button
                type="button"
                variant="outline"
                disabled={claimMutation.isPending}
                onClick={() => void changeClaim('release')}
              >
                Release case
              </Button>
            ) : null}
          </div>
        ) : null}
        {claimError ? <p role="alert">{claimError}</p> : null}
        {!queueItem && queue.hasNextPage ? (
          <Button
            type="button"
            variant="outline"
            disabled={queue.isFetchingNextPage}
            onClick={() => void queue.fetchNextPage()}
          >
            {queue.isFetchingNextPage ? 'Searching queue…' : 'Search more queue cases'}
          </Button>
        ) : null}
        <p
          ref={liveRegion}
          tabIndex={-1}
          aria-live="polite"
          className="text-sm text-muted-foreground"
        >
          {actionNotice}
        </p>
      </header>

      <section aria-labelledby="findings-heading" className="space-y-3">
        <h3 id="findings-heading" className="text-lg font-semibold">
          Findings and retrieval evidence
        </h3>
        {candidates.length === 0 ? (
          <p>No material retrieval findings were returned.</p>
        ) : (
          candidates.map((candidate, index) => (
            <article
              key={`${candidate.findingKey}-${index}`}
              className="space-y-2 rounded-md border p-4"
            >
              <h4 className="font-medium">{candidate.findingKey}</h4>
              <p>
                Finding type:{' '}
                {candidate.findingType
                  .replaceAll('_', ' ')
                  .toLowerCase()
                  .replace(/\b\w/g, (letter) => letter.toUpperCase())}
              </p>
              <p>
                File:{' '}
                <span className="break-all">
                  {candidate.scores?.queryFileId ?? candidate.ownScopePath}
                </span>
              </p>
              <p>
                Token rank: {candidate.scores?.tokenRank ?? 'Unavailable'}; token score:{' '}
                {candidate.scores?.tokenScore ?? 'Unavailable'}
              </p>
              <p>
                Semantic rank: {candidate.scores?.semanticRank ?? 'Unavailable'}; semantic score:{' '}
                {candidate.scores?.semanticScore ?? 'Unavailable'}
              </p>
              <p>
                {candidate.responseRequired
                  ? 'Seller response required'
                  : 'Seller response optional'}
                ; {candidate.answered ? 'answered' : 'unanswered'}
              </p>
              {candidate.responses.slice(0, 5).map((response) => (
                <blockquote
                  key={`${response.createdAt}-${response.kind}`}
                  className="max-h-32 overflow-auto whitespace-pre-wrap rounded bg-muted p-2 text-sm"
                >
                  <p>
                    {response.kind.replaceAll('_', ' ')} ·{' '}
                    {new Date(response.createdAt).toLocaleString()}
                  </p>
                  <p>{response.text.slice(0, 2000)}</p>
                </blockquote>
              ))}
              <div className="flex flex-wrap gap-2">
                {(['OWN_EXCERPT', 'FINDING_PROVENANCE'] as const).map((kind) => (
                  <Button
                    key={kind}
                    type="button"
                    variant="outline"
                    onClick={(event) => {
                      previewTrigger.current = event.currentTarget
                      setPreview({ kind, findingId: candidate.findingId })
                    }}
                  >
                    View {kind === 'OWN_EXCERPT' ? 'finding excerpt' : 'finding provenance'}
                  </Button>
                ))}
              </div>
            </article>
          ))
        )}
        {summary.retrievalEvidence?.candidates.some(
          (candidate) =>
            (candidate.findingType === 'SIMILARITY_TOKEN' ||
              candidate.findingType === 'SIMILARITY_SEMANTIC') &&
            !candidate.scores,
        ) ? (
          <p>Retrieval scores are unavailable for some findings.</p>
        ) : null}
      </section>

      <section aria-labelledby="evidence-heading" className="space-y-3">
        <h3 id="evidence-heading" className="text-lg font-semibold">
          Restricted evidence
        </h3>
        <p>
          Each preview is requested explicitly, audited by the service, and held only while this
          preview is mounted.
        </p>
        <div className="flex flex-wrap gap-2">
          {previewKinds.map((kind) => (
            <Button
              key={kind}
              type="button"
              variant="outline"
              onClick={(event) => {
                previewTrigger.current = event.currentTarget
                setPreview({ kind })
              }}
            >
              View {kind.replaceAll('_', ' ').toLowerCase()}
            </Button>
          ))}
        </div>
        {preview ? (
          <EvidencePreview
            key={`${preview.kind}:${preview.findingId ?? ''}`}
            submissionId={submissionId}
            kind={preview.kind}
            findingId={preview.findingId}
            close={() => setPreview(null)}
          />
        ) : null}
      </section>

      {coverage.isError ? <p role="alert">{moderationErrorMessage(coverage.error)}</p> : null}
      {coverage.hasNextPage ? (
        <Button
          type="button"
          variant="outline"
          disabled={coverage.isFetchingNextPage}
          onClick={() => void coverage.fetchNextPage()}
        >
          {coverage.isFetchingNextPage ? 'Loading requests…' : 'Load more coverage requests'}
        </Button>
      ) : null}
      {coverageRequest ? (
        <section aria-labelledby="coverage-heading" className="space-y-3">
          <h3 id="coverage-heading" className="text-lg font-semibold">
            Coverage assessment
          </h3>
          {coverageError ? <p role="alert">{coverageError}</p> : null}
          {assessMutation.isError &&
          assessMutation.error instanceof ModerationApiError &&
          assessMutation.error.status === 409 ? (
            <p role="status">
              The request was refreshed. Your assessment text remains in the form; review the
              current tuple above before retrying.
            </p>
          ) : null}
          {assessMutation.isError &&
          assessMutation.error instanceof ModerationApiError &&
          assessMutation.error.status === 409 ? (
            <aside aria-label="Refreshed case state" className="rounded border p-3 text-sm">
              <p>
                Current case: {summary.state.replaceAll('_', ' ')};{' '}
                {summary.kind === 'APPEAL' ? 'appeal' : 'standard'}; revision {summary.caseRevision}
                .
              </p>
              <p>
                Version {summary.assetVersionId}; report{' '}
                {summary.retrievalEvidence?.reportId ?? 'not linked'}.
              </p>
            </aside>
          ) : null}
          <form className="space-y-3" onSubmit={(event) => void submitCoverage(event)}>
            <label className="block">
              Outcome
              <select
                className="ml-2 rounded border bg-background p-2"
                value={outcome}
                onChange={(event) => setOutcome(event.target.value as typeof outcome)}
              >
                <option value="BLOCKED">Blocked</option>
                <option value="ACCEPTED">Accepted</option>
              </select>
            </label>
            <label className="block">
              Competence areas, comma-separated
              <input
                className="mt-1 block w-full rounded border bg-background p-2"
                value={competence}
                onChange={(event) => setCompetence(event.target.value)}
                required
              />
            </label>
            <label className="block">
              Review plan
              <textarea
                className="mt-1 block w-full rounded border bg-background p-2"
                value={reviewPlan}
                onChange={(event) => setReviewPlan(event.target.value)}
                required
                maxLength={2000}
              />
            </label>
            <label className="block">
              Rationale
              <textarea
                className="mt-1 block w-full rounded border bg-background p-2"
                value={rationale}
                onChange={(event) => setRationale(event.target.value)}
                required
                maxLength={2000}
              />
            </label>
            {outcome === 'ACCEPTED' ? (
              <>
                <label className="block">
                  Completed plan reference
                  <input
                    className="mt-1 block w-full rounded border bg-background p-2"
                    value={completedPlanReference}
                    onChange={(event) => setCompletedPlanReference(event.target.value)}
                    required
                    maxLength={200}
                  />
                </label>
                <label className="block">
                  Scope path
                  <input
                    className="mt-1 block w-full rounded border bg-background p-2"
                    value={scopePath}
                    onChange={(event) => setScopePath(event.target.value)}
                    required
                    maxLength={512}
                  />
                </label>
                <label className="block">
                  Language
                  <input
                    className="mt-1 block w-full rounded border bg-background p-2"
                    value={scopeLanguage}
                    onChange={(event) => setScopeLanguage(event.target.value)}
                    required
                    maxLength={60}
                  />
                </label>
                <label className="block">
                  Role in core logic
                  <input
                    className="mt-1 block w-full rounded border bg-background p-2"
                    value={scopeRole}
                    onChange={(event) => setScopeRole(event.target.value)}
                    required
                    maxLength={200}
                  />
                </label>
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    checked={scopeReadable}
                    onChange={(event) => setScopeReadable(event.target.checked)}
                  />
                  Evidence is readable
                </label>
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    checked={manageableScope}
                    onChange={(event) => setManageableScope(event.target.checked)}
                  />
                  Scope is manageable
                </label>
              </>
            ) : null}
            <Button type="submit" disabled={assessMutation.isPending || !competence.trim()}>
              {assessMutation.isPending ? 'Saving…' : 'Save assessment'}
            </Button>
          </form>
        </section>
      ) : summary.retrievalEvidence?.coverageLevel === 'PARTIAL' ||
        summary.retrievalEvidence?.coverageLevel === 'UNSUPPORTED' ? (
        <p role={coverage.isPending ? 'status' : 'note'}>
          {coverage.isPending
            ? 'Loading coverage request…'
            : coverage.hasNextPage
              ? 'Load more coverage requests to verify a unique request for this version and report.'
              : coverageMatches.length > 1
                ? 'Multiple coverage requests match this case; no assessment form is available.'
                : 'No matching coverage request is available for this case.'}
        </p>
      ) : null}

      <section aria-labelledby="decision-heading" className="space-y-3">
        <h3 id="decision-heading" className="text-lg font-semibold">
          Decision and dispositions
        </h3>
        {summary.retrievalEvidence?.responseRequirementsAvailable === false ? (
          <p role="alert">
            Required seller-response status is unavailable. Do not submit a decision.
          </p>
        ) : null}
        {candidates.some((candidate) => candidate.responseRequired && !candidate.answered) ? (
          <p role="status">Required seller responses are outstanding.</p>
        ) : null}
        {decisionMutation.isError ? (
          <p role="alert">{moderationErrorMessage(decisionMutation.error)}</p>
        ) : null}
        {decisionMutation.error instanceof ModerationApiError &&
        decisionMutation.error.blockerCodes.length ? (
          <ul aria-label="Decision blockers" className="list-disc pl-6">
            {decisionMutation.error.blockerCodes.map((code) => (
              <li key={code}>{decisionBlockerMessage(code)}</li>
            ))}
          </ul>
        ) : null}
        {decisionMutation.error instanceof ModerationApiError &&
        decisionMutation.error.status === 409 ? (
          <aside aria-label="Refreshed case state" className="rounded border p-3 text-sm">
            <p>
              Current case: {summary.state.replaceAll('_', ' ')}; revision {summary.caseRevision}.
            </p>
            <p>
              Version {summary.basis.versionId}; report {summary.basis.analysisReportId}; evidence
              revision {summary.basis.sellerEvidenceRevision}.
            </p>
          </aside>
        ) : null}
        <form className="space-y-3" onSubmit={(event) => void submitDecision(event)}>
          <label className="block">
            Decision
            <select
              aria-label="Decision"
              className="mt-1 block rounded border bg-background p-2"
              value={decisionOutcome}
              onChange={(event) => setDecisionOutcome(event.target.value as typeof decisionOutcome)}
            >
              <option value="">Select decision</option>
              <option value="APPROVE">Approve</option>
              {summary.kind !== 'APPEAL' ? (
                <option value="REQUEST_CHANGES">Request changes</option>
              ) : null}
              <option value="REJECT">Reject</option>
            </select>
          </label>
          <label className="block">
            Decision rationale
            <textarea
              aria-label="Decision rationale"
              className="mt-1 block w-full rounded border bg-background p-2"
              value={decisionRationale}
              onChange={(event) => setDecisionRationale(event.target.value)}
              maxLength={4000}
              required
            />
          </label>
          {candidates
            .filter((candidate) => candidate.responseRequired)
            .map((candidate) => (
              <fieldset key={candidate.findingId} className="space-y-2 rounded border p-3">
                <legend className="font-medium">Disposition: {candidate.findingKey}</legend>
                <label className="block">
                  Finding disposition
                  <select
                    aria-label={`Disposition for ${candidate.findingKey}`}
                    className="mt-1 block rounded border bg-background p-2"
                    value={dispositions[candidate.findingId]?.disposition ?? ''}
                    onChange={(event) =>
                      setDispositions((current) => ({
                        ...current,
                        [candidate.findingId]: {
                          disposition: event.target.value,
                          rationale: current[candidate.findingId]?.rationale ?? '',
                        },
                      }))
                    }
                    required
                  >
                    <option value="">Select disposition</option>
                    <option value="EXPLAINED">Explained</option>
                    <option value="FALSE_MATCH">False match</option>
                    <option value="ACCEPTED_WITH_LIMITS">Accepted with limits</option>
                    <option value="CHANGES_NEEDED">Changes needed</option>
                    <option value="INSUFFICIENT_EVIDENCE">Insufficient evidence</option>
                  </select>
                </label>
                <label className="block">
                  Disposition rationale
                  <textarea
                    aria-label={`Rationale for ${candidate.findingKey}`}
                    className="mt-1 block w-full rounded border bg-background p-2"
                    value={dispositions[candidate.findingId]?.rationale ?? ''}
                    onChange={(event) =>
                      setDispositions((current) => ({
                        ...current,
                        [candidate.findingId]: {
                          disposition: current[candidate.findingId]?.disposition ?? '',
                          rationale: event.target.value,
                        },
                      }))
                    }
                    maxLength={4000}
                    required
                  />
                </label>
              </fieldset>
            ))}
          {summary.manualPlanCompletionReferenceRequired ? (
            <label className="block">
              Confirm completed plan reference
              <input
                aria-label="Completed plan reference"
                className="mt-1 block w-full rounded border bg-background p-2"
                value={manualPlanReference ?? ''}
                onChange={(event) => setManualPlanReference(event.target.value)}
                maxLength={400}
                required
              />
              <span className="text-sm">
                Attest the completed plan reference required by the accepted assessment. The service
                compares the trimmed value; the reference itself is not shown here.
              </span>
            </label>
          ) : null}
          {!ownClaim ? <p role="status">Claim this case before submitting a decision.</p> : null}
          <Button
            type="submit"
            disabled={
              decisionMutation.isPending ||
              !ownClaim ||
              !decisionOutcome ||
              summary.retrievalEvidence?.responseRequirementsAvailable === false
            }
          >
            {decisionMutation.isPending ? 'Submitting decision…' : 'Submit decision'}
          </Button>
        </form>
        {summary.kind === 'APPEAL' ? (
          <p>Appeal cases allow approval or rejection. Request changes is not offered.</p>
        ) : null}
      </section>
    </main>
  )
}
