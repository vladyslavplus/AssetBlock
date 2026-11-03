import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ModerationCaseWorkspace } from './moderation-case-workspace'
import { renderWithProviders } from '@/test/render'
import { moderationKeys } from '@/lib/moderation/moderation-query'
import { moderationCaseSummarySchema } from '@/lib/moderation/moderation-schemas'

const id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const userId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const reportId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const hash = 'a'.repeat(64)
const user = {
  id: userId,
  username: 'moderator',
  role: 'Moderator',
  roleRevision: 1,
  emailVerifiedAt: '2026-01-01T00:00:00Z',
  avatarUrl: null,
  bio: null,
  isPublicProfile: false,
  createdAt: '2026-01-01T00:00:00Z',
  socialLinks: [],
}

function caseBody(kind: 'STANDARD' | 'APPEAL' = 'STANDARD', coverage = false, answered = false) {
  return {
    submissionId: id,
    assetId: id,
    assetVersionId: userId,
    state: 'IN_REVIEW',
    kind,
    caseRevision: 2,
    contentSha256: hash,
    policyVersion: 'policy-v1',
    basis: {
      versionId: userId,
      contentSha256: hash,
      analysisReportId: reportId,
      declarationRevision: 1,
      materialMetadataRevision: 1,
      policyVersion: 'policy-v1',
      sellerEvidenceRevision: 0,
      caseRevision: 2,
    },
    manualPlanCompletionReferenceRequired: false,
    isClaimedByCaller: true,
    coverageAssessmentId: null,
    retrievalEvidence: coverage
      ? {
          reportId,
          scoresAvailability: 'AVAILABLE',
          referenceSnapshotId: null,
          referenceRevision: null,
          coverageLevel: 'PARTIAL',
          omissionCount: 1,
          findingOverflow: 0,
          retrieval: null,
          candidates: [
            {
              findingId: id,
              findingKey: 'material-finding',
              findingType: 'SIMILARITY_SEMANTIC',
              ownScopePath: 'src/file.py',
              ownStartByte: null,
              ownEndByte: null,
              ownStartLine: null,
              ownEndLine: null,
              referenceItemId: null,
              referenceOrigin: null,
              referenceSensitivity: 'PUBLIC',
              scoresAvailability: 'AVAILABLE',
              scores: null,
              responseRequired: true,
              answered,
              responses: answered
                ? [
                    {
                      kind: 'PRIOR_WORK',
                      text: 'Seller response text',
                      createdAt: '2026-11-01T10:00:00Z',
                    },
                  ]
                : [],
            },
          ],
          responseRequirementsAvailable: true,
        }
      : null,
  }
}

function claimedQueue() {
  return {
    items: [
      {
        submissionId: id,
        assetId: id,
        assetTitle: 'Review asset',
        versionNumber: 1,
        submittedAt: '2026-11-01T10:00:00Z',
        state: 'IN_REVIEW',
        kind: 'STANDARD',
        claimHolderId: userId,
        claimHolderName: 'moderator',
        caseRevision: 2,
        coverageLevel: 'FULL',
        materialFindingCount: 1,
        hasOpenResponses: false,
      },
    ],
    nextCursor: null,
  }
}

function reviewRequest(revision = 3) {
  return {
    requestId: reportId,
    assetVersionId: userId,
    reportId,
    requestRevision: revision,
    tupleDigest: hash,
    createdAt: '2026-11-01T10:00:00Z',
  }
}

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('moderation case workspace', () => {
  afterEach(() => vi.unstubAllGlobals())

  it.each([
    [
      'ERR_MODERATION_APPEAL_DECISION_NOT_ALLOWED',
      'This decision is not allowed for an appeal case.',
    ],
    [
      'ERR_VERSION_FILE_CHECKS_INCOMPLETE',
      'This version does not have current security and processing checks.',
    ],
    ['ERR_REVIEW_COVERAGE_REQUIRED', 'An accepted coverage assessment is required.'],
    [
      'ERR_ANALYSIS_REPORT_NOT_TRUSTED',
      'The persisted analysis report failed its admission checks.',
    ],
    ['ERR_MODERATION_WORKSPACE_STALE', 'The submission workspace changed after analysis.'],
    ['ERR_DECLARATION_INCOMPLETE', 'The frozen source declaration is incomplete.'],
    ['ERR_COVERAGE_ASSESSMENT_INVALIDATED', 'The coverage assessment is no longer current.'],
    ['ERR_COVERAGE_ASSESSOR_UNAVAILABLE', 'Coverage review needs a new eligible reviewer.'],
    [
      'ERR_COVERAGE_ASSESSMENT_BLOCKED',
      'The accepted coverage assessment no longer permits this decision.',
    ],
    [
      'ERR_REVIEW_PLAN_REFERENCE_MISMATCH',
      'Confirm the exact completed plan reference from the assessment.',
    ],
    ['ERR_ANALYSIS_NOT_AVAILABLE', 'Required analysis or response status is unavailable.'],
    [
      'ERR_MODERATION_DECISION_RESPONSES_OUTSTANDING',
      'Seller responses to required findings are outstanding.',
    ],
    [
      'ERR_MODERATION_DISPOSITIONS_INVALID',
      'Provide one valid disposition for every material finding.',
    ],
    ['ERR_MODERATION_DECISION_BLOCKED', 'One or more dispositions prevent approval.'],
    ['ERR_PUBLICATION_METADATA_UNAVAILABLE', 'Required publication metadata is unavailable.'],
    [
      'ERR_MODERATION_CONCURRENT_UPDATE',
      'The case changed during the decision. Review its refreshed state.',
    ],
    [
      'ERR_FUTURE_REVIEW_BLOCKER',
      'The backend blocked this decision. Review the refreshed case before retrying.',
    ],
  ])('renders the 409 decision blocker message for %s', async (code, expectedMessage) => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (init?.method === 'POST')
        return json({ code: 'ERR_MODERATION_DECISION_BLOCKED', blockerCodes: [code] }, 409)
      if (url.includes(`/submissions/${id}`)) return json(caseBody('STANDARD', true, true))
      if (url.includes('/submissions?')) return json(claimedQueue())
      if (url.includes('coverage-reviews'))
        return json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
      return json({ items: [], nextCursor: null })
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    fireEvent.change(await screen.findByLabelText('Decision rationale'), {
      target: { value: 'Decision rationale for current claimed case.' },
    })
    fireEvent.change(screen.getByLabelText('Disposition for material-finding'), {
      target: { value: 'EXPLAINED' },
    })
    fireEvent.change(screen.getByLabelText('Rationale for material-finding'), {
      target: { value: 'Current finding rationale.' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Submit decision' }))
    expect(await screen.findByLabelText('Refreshed case state')).toBeInTheDocument()
    expect(await screen.findByText(expectedMessage)).toBeInTheDocument()
    expect(screen.queryByText(code)).not.toBeInTheDocument()
  })

  it('shows case loading and API error states', async () => {
    let finish: ((response: Response) => void) | undefined
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) =>
        String(input).includes(`/submissions/${id}`)
          ? new Promise<Response>((resolve) => {
              finish = resolve
            })
          : Promise.resolve(json({ items: [], nextCursor: null })),
      ),
    )
    const view = renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, {
      authUser: user,
    })
    expect(screen.getByRole('status')).toHaveTextContent('Loading case')
    finish?.(json(caseBody()))
    expect(
      await screen.findByRole('heading', { name: 'Findings and retrieval evidence' }),
    ).toBeInTheDocument()
    expect(screen.getByText('No material retrieval findings were returned.')).toBeInTheDocument()
    view.unmount()

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) =>
        String(input).includes(`/submissions/${id}`)
          ? json({ code: 'ERR_MODERATION_CASE_NOT_FOUND' }, 404)
          : json({ items: [], nextCursor: null }),
      ),
    )
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    expect(await screen.findByRole('heading', { name: 'Could not load case' })).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('This moderation case was not found.')
  })

  it('shows appeal kind and does not offer request changes', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) =>
        String(input).includes(`/submissions/${id}`)
          ? json(caseBody('APPEAL'))
          : String(input).includes('coverage-reviews')
            ? json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
            : json({ items: [], nextCursor: null }),
      ),
    )
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    expect(await screen.findByText('Appeal', { selector: 'dd' })).toBeInTheDocument()
    expect(
      screen.getByText('Appeal cases allow approval or rejection. Request changes is not offered.'),
    ).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: /request changes/i })).not.toBeInTheDocument()
  })

  it('renders seller responses and disables decision when moderator does not hold case', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url.includes(`/submissions/${id}`))
          return json({ ...caseBody('STANDARD', true, true), isClaimedByCaller: false })
        if (url.includes('/submissions?'))
          return json({
            ...claimedQueue(),
            items: [
              { ...claimedQueue().items[0], claimHolderId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc' },
            ],
          })
        if (url.includes('coverage-reviews'))
          return json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
        return json({ items: [], nextCursor: null })
      }),
    )
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    expect(await screen.findByText('Seller response text')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Submit decision' })).toBeDisabled()
  })

  it('shows outstanding seller responses and keeps decision submission blocked', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
      const url = String(input)
      if (url.includes(`/submissions/${id}`)) return json(caseBody('STANDARD', true, false))
      if (url.includes('/submissions?')) return json(claimedQueue())
      if (url.includes('coverage-reviews'))
        return json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
      return json({ items: [], nextCursor: null })
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    expect(
      await screen.findByText('Required seller responses are outstanding.'),
    ).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Decision rationale'), {
      target: { value: 'Decision text.' },
    })
    fireEvent.change(screen.getByLabelText('Disposition for material-finding'), {
      target: { value: 'EXPLAINED' },
    })
    fireEvent.change(screen.getByLabelText('Rationale for material-finding'), {
      target: { value: 'Explanation.' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Submit decision' }))
    expect(
      screen.getByText(
        'Required seller responses are still outstanding. The case cannot be decided yet.',
      ),
    ).toBeInTheDocument()
    expect(
      fetchMock.mock.calls.some((call) => (call[1] as RequestInit | undefined)?.method === 'POST'),
    ).toBe(false)
  })

  it('preserves decision text on 409 and renders backend blocker codes', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (init?.method === 'POST')
        return json(
          {
            code: 'ERR_MODERATION_DECISION_BLOCKED',
            blockerCodes: ['ERR_MODERATION_DECISION_BLOCKED'],
          },
          409,
        )
      if (url.includes(`/submissions/${id}`)) return json(caseBody('STANDARD', true, true))
      if (url.includes('/submissions?')) return json(claimedQueue())
      if (url.includes('coverage-reviews'))
        return json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
      return json({ items: [], nextCursor: null })
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    fireEvent.change(await screen.findByLabelText('Decision rationale'), {
      target: { value: 'Keep this decision explanation.' },
    })
    fireEvent.change(screen.getByLabelText('Disposition for material-finding'), {
      target: { value: 'EXPLAINED' },
    })
    fireEvent.change(screen.getByLabelText('Rationale for material-finding'), {
      target: { value: 'Keep this finding explanation.' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Submit decision' }))
    expect(await screen.findByLabelText('Refreshed case state')).toBeInTheDocument()
    expect(screen.getByLabelText('Decision rationale')).toHaveValue(
      'Keep this decision explanation.',
    )
    expect(screen.getByLabelText('Rationale for material-finding')).toHaveValue(
      'Keep this finding explanation.',
    )
    expect(screen.getByText('One or more dispositions prevent approval.')).toBeInTheDocument()
  })

  it('reuses operation ID for retry of unchanged decision attempt', async () => {
    let writes = 0
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (init?.method === 'POST') {
        writes += 1
        return writes === 1
          ? json({ code: 'ERR_REVIEW_AUDIT_UNAVAILABLE' }, 503)
          : json({
              decisionId: reportId,
              state: 'REJECTED',
              publicationSnapshotId: null,
              promoted: false,
              caseRevision: 3,
              replayed: false,
            })
      }
      if (url.includes(`/submissions/${id}`)) return json(caseBody('STANDARD', true, true))
      if (url.includes('/submissions?')) return json(claimedQueue())
      if (url.includes('coverage-reviews'))
        return json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
      return json({ items: [], nextCursor: null })
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    fireEvent.change(await screen.findByLabelText('Decision rationale'), {
      target: { value: 'Decision text.' },
    })
    fireEvent.change(screen.getByLabelText('Disposition for material-finding'), {
      target: { value: 'FALSE_MATCH' },
    })
    fireEvent.change(screen.getByLabelText('Rationale for material-finding'), {
      target: { value: 'No match.' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Submit decision' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be audited')
    fireEvent.click(screen.getByRole('button', { name: 'Submit decision' }))
    await screen.findByText('Decision saved.')
    const bodies = fetchMock.mock.calls
      .filter((call) => call[1]?.method === 'POST')
      .map(
        (call) =>
          JSON.parse(call[1]?.body as string) as {
            operationId: string
            basis: unknown
            dispositions: unknown[]
          },
      )
    expect(bodies).toHaveLength(2)
    expect(bodies[0]?.operationId).toBe(bodies[1]?.operationId)
    expect(bodies[0]?.basis).toEqual(caseBody('STANDARD', true, true).basis)
    expect(bodies[0]?.dispositions).toHaveLength(1)
  })

  it('preserves assessment text and shows refreshed case after a coverage conflict', async () => {
    let writes = 0
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        if (init?.method === 'POST') {
          writes += 1
          return json({ code: 'ERR_COVERAGE_REVIEW_CONFLICT' }, 409)
        }
        if (url.includes(`/submissions/${id}`)) return json(caseBody('STANDARD', true))
        if (url.includes('coverage-reviews'))
          return json({
            items: [reviewRequest(writes ? 4 : 3)],
            page: 1,
            pageSize: 20,
            totalCount: 1,
            totalPages: 1,
          })
        return json({ items: [], nextCursor: null })
      }),
    )
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    const rationale = await screen.findByLabelText('Rationale')
    fireEvent.change(rationale, { target: { value: 'Preserve this explanation.' } })
    fireEvent.change(screen.getByLabelText('Review plan'), {
      target: { value: 'Inspect omitted source files.' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Save assessment' }))
    expect(
      await screen.findByText(
        'The request was refreshed. Your assessment text remains in the form; review the current tuple above before retrying.',
      ),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('Rationale')).toHaveValue('Preserve this explanation.')
    expect(screen.getAllByText(id, { selector: 'dd' })).toHaveLength(2)
    expect(writes).toBe(1)
  })

  it('reuses the operation ID when retrying the same assessment attempt', async () => {
    let writes = 0
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (init?.method === 'POST') {
        writes += 1
        return writes === 1
          ? json({ code: 'ERR_REVIEW_AUDIT_UNAVAILABLE' }, 503)
          : json({
              requestId: reportId,
              requestRevision: 4,
              workspaceRevision: 5,
              tupleDigest: hash,
              replayed: false,
            })
      }
      if (url.includes(`/submissions/${id}`)) return json(caseBody('STANDARD', true))
      if (url.includes('coverage-reviews'))
        return json({
          items: [reviewRequest()],
          page: 1,
          pageSize: 20,
          totalCount: 1,
          totalPages: 1,
        })
      return json({ items: [], nextCursor: null })
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    fireEvent.change(await screen.findByLabelText('Rationale'), {
      target: { value: 'Same attempt.' },
    })
    fireEvent.change(screen.getByLabelText('Review plan'), { target: { value: 'Inspect source.' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save assessment' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('No content was released')
    fireEvent.click(screen.getByRole('button', { name: 'Save assessment' }))
    await screen.findByText('Coverage assessment saved.')
    const bodies = fetchMock.mock.calls
      .filter((call) => call[1]?.method === 'POST')
      .map((call) => JSON.parse(call[1]?.body as string) as { operationId: string })
    expect(bodies).toHaveLength(2)
    expect(bodies[0]?.operationId).toBe(bodies[1]?.operationId)
  })

  it('discards restricted preview content when preview component unmounts', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) =>
      String(input).includes('/evidence/')
        ? json({
            scopeId: id,
            reportId,
            kind: 'DECLARATION',
            findingId: null,
            content: 'preview payload',
            itemCount: 1,
            truncated: false,
          })
        : String(input).includes(`/submissions/${id}`)
          ? json(caseBody())
          : String(input).includes('coverage-reviews')
            ? json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
            : json({ items: [], nextCursor: null }),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    fireEvent.click(await screen.findByRole('button', { name: 'View declaration' }))
    expect(await screen.findByText('preview payload')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Close preview' }))
    await waitFor(() => expect(screen.queryByText('preview payload')).not.toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: 'View declaration' }))
    expect(await screen.findByText('preview payload')).toBeInTheDocument()
    expect(
      fetchMock.mock.calls.filter((call) => String(call[0]).includes('/evidence/')),
    ).toHaveLength(2)
  })

  it('accepts and labels finding types from the strict case schema', async () => {
    const body = caseBody('STANDARD', true, true)
    const candidate = body.retrievalEvidence?.candidates.at(0)
    if (!candidate) throw new Error('Case fixture needs one finding.')
    candidate.findingType = 'NOTICE_MISSING'
    expect(moderationCaseSummarySchema.safeParse(body).success).toBe(true)
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) =>
        String(input).includes(`/submissions/${id}`)
          ? json(body)
          : String(input).includes('coverage-reviews')
            ? json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
            : json({ items: [], nextCursor: null }),
      ),
    )
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    expect(await screen.findByText(/Finding type: Notice Missing/)).toBeInTheDocument()
  })

  it('requires Moderator-entered plan attestation and sends typed value unchanged', async () => {
    const planCase = {
      ...caseBody('STANDARD', true, true),
      manualPlanCompletionReferenceRequired: true,
    }
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST')
        return json({
          decisionId: id,
          state: 'APPROVED',
          publicationSnapshotId: null,
          promoted: false,
          caseRevision: 3,
          replayed: false,
        })
      if (String(input).includes(`/submissions/${id}`)) return json(planCase)
      if (String(input).includes('/submissions?')) return json(claimedQueue())
      if (String(input).includes('coverage-reviews'))
        return json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
      return json({ items: [], nextCursor: null })
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    const reference = await screen.findByLabelText('Completed plan reference')
    expect(reference).toHaveValue('')
    fireEvent.change(screen.getByLabelText('Decision rationale'), {
      target: { value: 'Reviewed.' },
    })
    fireEvent.change(screen.getByLabelText('Disposition for material-finding'), {
      target: { value: 'EXPLAINED' },
    })
    fireEvent.change(screen.getByLabelText('Rationale for material-finding'), {
      target: { value: 'Explained.' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Submit decision' }))
    expect(fetchMock.mock.calls.some((call) => call[1]?.method === 'POST')).toBe(false)
    fireEvent.change(reference, { target: { value: '  manual/review-complete  ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Submit decision' }))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some((call) => call[1]?.method === 'POST')).toBe(true),
    )
    const body = JSON.parse(
      fetchMock.mock.calls.find((call) => call[1]?.method === 'POST')?.[1]?.body as string,
    )
    expect(body.manualPlanCompletionReference).toBe('  manual/review-complete  ')
  })

  it('clears prior evidence while changing preview kind and after next preview fails', async () => {
    let resolveDeclaration: ((response: Response) => void) | undefined
    let rejectSellerEvidence: ((reason: Error) => void) | undefined
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) => {
        const url = String(input)
        if (url.includes('/evidence/DECLARATION'))
          return new Promise<Response>((resolve) => {
            resolveDeclaration = resolve
          })
        if (url.includes('/evidence/SELLER_EVIDENCE'))
          return new Promise<Response>((_resolve, reject) => {
            rejectSellerEvidence = reject
          })
        if (url.includes(`/submissions/${id}`)) return Promise.resolve(json(caseBody()))
        if (url.includes('coverage-reviews'))
          return Promise.resolve(
            json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 }),
          )
        return Promise.resolve(json({ items: [], nextCursor: null }))
      }),
    )
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    await screen.findByRole('heading', { name: 'Restricted evidence' })
    fireEvent.click(screen.getByRole('button', { name: 'View declaration' }))
    await waitFor(() => expect(resolveDeclaration).toBeTypeOf('function'))
    resolveDeclaration?.(
      json({
        scopeId: id,
        reportId,
        kind: 'DECLARATION',
        findingId: null,
        content: 'declaration private text',
        itemCount: 1,
        truncated: false,
      }),
    )
    expect(await screen.findByText('declaration private text')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'View seller evidence' }))
    expect(screen.queryByText('declaration private text')).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Loading evidence preview')
    await waitFor(() => expect(rejectSellerEvidence).toBeTypeOf('function'))
    rejectSellerEvidence?.(new Error('Preview unavailable'))
    expect(await screen.findByRole('alert')).toHaveTextContent('Preview unavailable')
    expect(screen.queryByText('declaration private text')).not.toBeInTheDocument()
  })

  it('does not submit request changes after refreshed case becomes an appeal', async () => {
    let caseReads = 0
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (init?.method === 'POST')
        return json({
          submissionId: id,
          state: 'IN_REVIEW',
          holderId: userId,
          caseRevision: 3,
          replayed: false,
        })
      if (url.includes(`/submissions/${id}`))
        return json(caseBody(++caseReads === 1 ? 'STANDARD' : 'APPEAL'))
      if (url.includes('/submissions?')) return json(claimedQueue())
      if (url.includes('coverage-reviews'))
        return json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
      return json({ items: [], nextCursor: null })
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    fireEvent.change(await screen.findByLabelText('Decision'), {
      target: { value: 'REQUEST_CHANGES' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Release case' }))
    await waitFor(() => expect(screen.getByText('Appeal', { selector: 'dd' })).toBeInTheDocument())
    expect(screen.getByLabelText('Decision')).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Submit decision' })).toBeDisabled()
    expect(fetchMock.mock.calls.filter((call) => call[1]?.method === 'POST')).toHaveLength(1)
  })

  it('does not poll audited case reads or refetch them on focus or reconnect', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) =>
        String(input).includes(`/submissions/${id}`)
          ? json(caseBody())
          : String(input).includes('coverage-reviews')
            ? json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
            : json({ items: [], nextCursor: null }),
      ),
    )
    const { queryClient } = renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, {
      authUser: user,
    })
    await screen.findByRole('heading', { name: 'Findings and retrieval evidence' })
    const query = queryClient.getQueryCache().find({ queryKey: moderationKeys.case(user.id, id) })
    const options = query?.options as unknown as {
      refetchInterval?: unknown
      refetchOnWindowFocus?: unknown
      refetchOnReconnect?: unknown
    }
    expect(options.refetchInterval).toBe(false)
    expect(options.refetchOnWindowFocus).toBe(false)
    expect(options.refetchOnReconnect).toBe(false)
  })

  it('disables claim while a request is pending and prevents double writes', async () => {
    const caseResponse = { ...caseBody(), isClaimedByCaller: false }
    let finish: ((response: Response) => void) | undefined
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST')
        return new Promise<Response>((resolve) => {
          finish = resolve
        })
      if (String(input).includes(`/submissions/${id}`)) return Promise.resolve(json(caseResponse))
      if (String(input).includes('coverage-reviews'))
        return Promise.resolve(
          json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 }),
        )
      if (String(input).includes('/submissions?'))
        return Promise.resolve(
          json({
            items: [{ ...claimedQueue().items[0], claimHolderId: null, claimHolderName: null }],
            nextCursor: null,
          }),
        )
      return Promise.resolve(json({ items: [], nextCursor: null }))
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    const claim = await screen.findByRole('button', { name: 'Claim case' })
    fireEvent.click(claim)
    await waitFor(() => expect(claim).toBeDisabled())
    fireEvent.click(claim)
    expect(fetchMock.mock.calls.filter((call) => call[1]?.method === 'POST')).toHaveLength(1)
    finish?.(
      json({
        submissionId: id,
        state: 'IN_REVIEW',
        holderId: userId,
        caseRevision: 3,
        replayed: false,
      }),
    )
  })

  it('does not allow unknown caller identity to count as holding the claim', async () => {
    const body = { ...caseBody(), isClaimedByCaller: false }
    const fetchMock = vi.fn(async (input: RequestInfo | URL) =>
      String(input).includes(`/submissions/${id}`)
        ? json(body)
        : String(input).includes('coverage-reviews')
          ? json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
          : json({ items: [], nextCursor: null }),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: null })
    expect(screen.getByRole('status')).toHaveTextContent('Loading case')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('disables release while a request is pending and prevents a second release', async () => {
    let finish: ((response: Response) => void) | undefined
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST')
        return new Promise<Response>((resolve) => {
          finish = resolve
        })
      if (String(input).includes(`/submissions/${id}`)) return Promise.resolve(json(caseBody()))
      if (String(input).includes('coverage-reviews'))
        return Promise.resolve(
          json({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 }),
        )
      return Promise.resolve(json(claimedQueue()))
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationCaseWorkspace submissionId={id} />, { authUser: user })
    const release = await screen.findByRole('button', { name: 'Release case' })
    fireEvent.click(release)
    await waitFor(() => expect(release).toBeDisabled())
    fireEvent.click(release)
    expect(fetchMock.mock.calls.filter((call) => call[1]?.method === 'POST')).toHaveLength(1)
    finish?.(
      json({
        submissionId: id,
        state: 'SUBMITTED',
        holderId: null,
        caseRevision: 3,
        replayed: false,
      }),
    )
  })
})
