import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SellerVersionReviewPanel } from './seller-version-review-panel'
import { renderWithQueryClient } from '@/test/render'
import type { SellerVersionReview } from '@/lib/seller/seller-draft-schemas'
import { notificationsKeys } from '@/lib/notifications/notifications-query'
import { sellerKeys } from '@/lib/seller/seller-query'

const assetId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const versionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const submissionId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const reportId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'

const responses = {
  workspaceId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
  workspaceRevision: 7,
  caseRevision: 3,
  evidenceRevision: 2,
  reportId,
  requiredCount: 0,
  outstandingCount: 0,
  evidenceText: '',
  items: [],
  totalCount: 0,
  page: 1,
  pageSize: 50,
  totalPages: 0,
}

function review(overrides: Record<string, unknown> = {}): SellerVersionReview {
  return {
    coverageReview: null,
    requiredResponseCount: 0,
    outstandingResponseCount: 0,
    evidenceRevision: 2,
    assetId,
    assetVersionId: versionId,
    versionNumber: 2,
    processingStatus: 'READY',
    processingErrorCode: null,
    processingErrorSummary: null,
    analysis: 'ANALYSIS_AVAILABLE',
    moderationState: 'REJECTED',
    publicationEligible: false,
    canUpload: true,
    canSubmit: false,
    canAppeal: false,
    appeal: null,
    activeSubmission: null,
    history: [],
    blockedReasons: ['ERR_MODERATION_APPEAL_REQUIRED'],
    decision: {
      outcome: 'REJECTED',
      reason: 'Please correct the declared source attribution.',
      decidedAt: '2026-10-31T10:00:00Z',
      publication: null,
    },
    analysisDetails: {
      coverageReview: null,
      requiredResponseCount: 0,
      outstandingResponseCount: 0,
      evidenceRevision: 2,
      assetId,
      assetVersionId: versionId,
      lifecycle: 'COMPLETED',
      runId: reportId,
      reportId,
      reportIsCurrent: true,
      coverageLevel: 'FULL',
      analyzedFileCount: 1,
      omissionCount: 0,
      findingCount: 0,
      findingDetailsAvailable: true,
      createdAt: '2026-10-31T09:00:00Z',
      completedAt: '2026-10-31T09:01:00Z',
      errorCode: null,
      errorMessage: null,
      actionHint: null,
      canRetry: false,
      retryExpectation: null,
      canSubmit: false,
      reviewEligible: true,
      submissionBlockedReason: null,
      comparisonScope: 'Selected source files from this version only',
      limitations: [
        'Pair verification is disabled by evaluation. Retained comparison evidence requires human review.',
      ],
      stages: [
        {
          stage: 'VERIFIER',
          applicability: 'OPTIONAL',
          outcome: 'NOT_APPLICABLE',
          errorCode: 'POLICY_DISABLED',
          retryable: false,
        },
      ],
    },
    ...overrides,
  } as SellerVersionReview
}

function renderPanel(value = review()) {
  return renderWithQueryClient(
    <SellerVersionReviewPanel assetId={assetId} versionId={versionId} review={value} />,
  )
}

describe('seller version review panel', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  it('shows owner-safe decision, human review policy, and rejected-basis guidance', () => {
    renderPanel()

    expect(screen.getByRole('region', { name: 'Review status' })).toBeInTheDocument()
    expect(screen.getByText('Rejected')).toBeInTheDocument()
    expect(screen.getByText('Please correct the declared source attribution.')).toBeInTheDocument()
    expect(screen.getByText(/human review/i)).toBeInTheDocument()
    expect(
      screen.getByText(/request an independent review of the rejection instead/i),
    ).toBeInTheDocument()
    expect(screen.queryByText(/moderator/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/disposition|score|reviewer id/i)).not.toBeInTheDocument()
  })

  it('uses the structured verifier policy signal even when limitation wording changes', () => {
    const value = review()
    const analysisDetails = value.analysisDetails
    if (!analysisDetails) throw new Error('Expected fixture analysis details.')
    value.analysisDetails = {
      ...analysisDetails,
      limitations: ['A revised human review policy description.'],
      stages: [
        {
          stage: 'VERIFIER',
          applicability: 'OPTIONAL',
          outcome: 'NOT_APPLICABLE',
          errorCode: 'POLICY_DISABLED',
          retryable: false,
        },
      ],
    }

    renderPanel(value)

    expect(
      screen.getByText(/pair verification was disabled by the fixed evaluation policy/i),
    ).toBeInTheDocument()
  })

  it('does not display a reason on an approved decision', () => {
    renderPanel({
      ...review(),
      moderationState: 'APPROVED',
      decision: {
        outcome: 'APPROVED',
        reason: 'Unexpected private rationale.',
        decidedAt: '2026-10-31T10:00:00Z',
        publication: 'HISTORICAL',
      },
    })

    expect(screen.getByText(/publication: approved, historical version/i)).toBeInTheDocument()
    expect(screen.queryByText('Unexpected private rationale.')).not.toBeInTheDocument()
  })

  it.each(['PENDING_REVIEW', 'UPHELD', 'APPROVED', 'WITHDRAWN'] as const)(
    'shows appeal state %s without reviewer details',
    (state) => {
      renderPanel({
        ...review(),
        canAppeal: false,
        appeal: { state, requestedAt: '2026-10-31T10:00:00Z', decidedAt: null },
      })
      expect(screen.getByLabelText('Appeal status')).toHaveTextContent(state.replaceAll('_', ' '))
      expect(screen.queryByRole('textbox', { name: 'Appeal rationale' })).not.toBeInTheDocument()
    },
  )

  it('offers appeal only when canAppeal is true and retains rationale after a 409', async () => {
    const writes: Array<Record<string, unknown>> = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          writes.push(JSON.parse(init.body as string))
          return new Response(
            JSON.stringify({
              code: 'ERR_MODERATION_APPEAL_BASIS_CHANGED',
              detail: 'The review basis changed. Submit a new review request.',
            }),
            { status: 409 },
          )
        }
        return new Response(JSON.stringify(responses))
      }),
    )
    vi.stubGlobal('crypto', { randomUUID: () => submissionId })
    const rendered = renderPanel({ ...review(), canAppeal: true, blockedReasons: [] })
    const invalidation = vi.spyOn(rendered.queryClient, 'invalidateQueries')

    const rationale = await screen.findByRole('textbox', { name: 'Appeal rationale' })
    fireEvent.change(rationale, { target: { value: '  The source is independently licensed.  ' } })
    expect(screen.getByText(/37 \/ 2000 characters after trimming/)).toBeInTheDocument()
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Request another review' })).toBeEnabled(),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Request another review' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/submit a new review request/i)
    expect(screen.getByRole('textbox', { name: 'Appeal rationale' })).toHaveValue(
      '  The source is independently licensed.  ',
    )
    expect(writes).toHaveLength(1)
    expect(writes[0]).toMatchObject({
      workspaceId: responses.workspaceId,
      expectedWorkspaceRevision: responses.workspaceRevision,
      expectedCaseRevision: responses.caseRevision,
      operationId: submissionId,
      rationale: 'The source is independently licensed.',
    })
    expect(invalidation).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: notificationsKeys.all }),
    )
  })

  it('reuses the appeal operation ID when retrying the unchanged attempt', async () => {
    const writes: Array<Record<string, unknown>> = []
    vi.stubGlobal('crypto', { randomUUID: () => submissionId })
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          writes.push(JSON.parse(init.body as string))
          if (writes.length === 1) throw new TypeError('Connection lost')
          return new Response(JSON.stringify({ submissionId }))
        }
        return new Response(
          JSON.stringify(
            writes.length ? { ...responses, workspaceRevision: 8, caseRevision: 4 } : responses,
          ),
        )
      }),
    )
    renderPanel({ ...review(), canAppeal: true, blockedReasons: [] })

    fireEvent.change(await screen.findByRole('textbox', { name: 'Appeal rationale' }), {
      target: { value: 'Independent review requested.' },
    })
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Request another review' })).toBeEnabled(),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Request another review' }))
    await screen.findByRole('button', { name: 'Retry appeal request' })
    fireEvent.click(screen.getByRole('button', { name: 'Retry appeal request' }))
    await waitFor(() =>
      expect(screen.getByRole('status')).toHaveTextContent(/appeal request submitted/i),
    )

    expect(writes).toHaveLength(2)
    expect(writes[1]).toEqual(writes[0])
    expect(writes[1]).toMatchObject({
      expectedWorkspaceRevision: responses.workspaceRevision,
      expectedCaseRevision: responses.caseRevision,
    })
  })

  it('shows bounded seller submission history with decision reasons as text', () => {
    renderPanel({
      ...review(),
      history: [
        {
          submissionId,
          kind: 'APPEAL',
          state: 'REJECTED',
          decisionOutcome: 'REJECTED',
          decisionReason: 'The evidence does not resolve the concern.',
          createdAt: '2026-10-31T10:00:00Z',
          decidedAt: '2026-10-31T11:00:00Z',
        },
      ],
    })

    expect(screen.getByRole('list', { name: 'Review history' })).toHaveTextContent('Appeal')
    expect(screen.getByRole('list', { name: 'Review history' })).toHaveTextContent('Rejected')
    expect(screen.getByText('The evidence does not resolve the concern.')).toBeInTheDocument()
    expect(screen.queryByText(/moderator|claim|disposition|score/i)).not.toBeInTheDocument()
  })

  it.each([
    ['STANDARD', 'Withdraw review request'],
    ['APPEAL', 'Withdraw appeal'],
  ] as const)(
    'withdraws an active %s submission once despite a double click',
    async (kind, label) => {
      const writes: Array<{ url: string; body: Record<string, unknown> }> = []
      vi.stubGlobal('crypto', { randomUUID: () => reportId })
      vi.stubGlobal(
        'fetch',
        vi.fn(async (url: string, init?: RequestInit) => {
          if (init?.method === 'POST') {
            writes.push({ url, body: JSON.parse(init.body as string) })
            await new Promise((resolve) => setTimeout(resolve, 20))
            return new Response(
              JSON.stringify({
                status: 'SUCCEEDED',
                caseRevision: 5,
                idempotentResultJson: null,
                replayed: false,
              }),
            )
          }
          return new Response(JSON.stringify(responses))
        }),
      )
      const value = review({
        activeSubmission: { submissionId, kind, state: 'IN_REVIEW', caseRevision: 4 },
      })
      const rendered = renderPanel(value)
      const invalidation = vi.spyOn(rendered.queryClient, 'invalidateQueries')
      const button = screen.getByRole('button', { name: label })

      fireEvent.click(button)
      fireEvent.click(button)
      await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(/withdrawn/i))

      expect(writes).toHaveLength(1)
      expect(writes[0]?.url).toContain(`/api/seller/submissions/${submissionId}/withdraw`)
      expect(writes[0]?.body).toEqual({ expectedCaseRevision: 4, operationId: reportId })
      expect(invalidation).toHaveBeenCalledWith(
        expect.objectContaining({ queryKey: sellerKeys.versionReview(assetId, versionId) }),
      )
      expect(invalidation).toHaveBeenCalledWith(
        expect.objectContaining({ queryKey: notificationsKeys.all }),
      )
    },
  )

  it('keeps an active withdrawal visible and reports a stale conflict', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) =>
        init?.method === 'POST'
          ? new Response(
              JSON.stringify({
                code: 'ERR_SUBMISSION_WITHDRAW_CONFLICT',
                detail: 'The submission case changed. Refresh and try again.',
              }),
              { status: 409 },
            )
          : new Response(JSON.stringify(responses)),
      ),
    )
    renderPanel({
      ...review(),
      activeSubmission: { submissionId, kind: 'STANDARD', state: 'SUBMITTED', caseRevision: 9 },
    })

    fireEvent.click(screen.getByRole('button', { name: 'Withdraw review request' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/submission case changed/i)
    expect(screen.getByRole('button', { name: 'Withdraw review request' })).toBeInTheDocument()
  })

  it('reuses a withdrawal operation ID when retrying the same active case', async () => {
    const writes: Array<Record<string, unknown>> = []
    vi.stubGlobal('crypto', { randomUUID: () => reportId })
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          writes.push(JSON.parse(init.body as string))
          if (writes.length === 1) throw new TypeError('Connection lost')
          return new Response(
            JSON.stringify({
              status: 'SUCCEEDED',
              caseRevision: 5,
              idempotentResultJson: null,
              replayed: false,
            }),
          )
        }
        return new Response(JSON.stringify(responses))
      }),
    )
    renderPanel({
      ...review(),
      activeSubmission: { submissionId, kind: 'STANDARD', state: 'SUBMITTED', caseRevision: 4 },
    })

    fireEvent.click(screen.getByRole('button', { name: 'Withdraw review request' }))
    await screen.findByRole('button', { name: 'Retry withdrawal' })
    fireEvent.click(screen.getByRole('button', { name: 'Retry withdrawal' }))
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(/withdrawn/i))

    expect(writes).toHaveLength(2)
    expect(writes[1]).toEqual(writes[0])
  })

  it('hides withdrawal controls when no active submission exists', () => {
    renderPanel({ ...review(), activeSubmission: null })

    expect(screen.queryByRole('button', { name: /withdraw/i })).not.toBeInTheDocument()
  })
})
