import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  fetchCoverageRequests,
  fetchModerationEvidence,
  fetchModerationQueue,
  ModerationApiError,
  moderationErrorMessage,
  postCoverageAssessment,
} from './moderation-query'
import { moderationBackendEndpoints } from './moderation-routes'

afterEach(() => vi.unstubAllGlobals())

describe('moderation data contracts', () => {
  it('rejects invalid successful responses with a gateway error', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify({ items: [], nextCursor: null, extra: true }))),
    )
    await expect(fetchModerationQueue()).rejects.toMatchObject({ status: 502 })
  })

  it('strictly validates coverage and evidence responses and requests evidence without caching', async () => {
    const evidence = {
      scopeId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      reportId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      kind: 'DECLARATION',
      findingId: null,
      content: 'safe text',
      itemCount: 1,
      truncated: false,
    }
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) =>
      String(input).includes('/evidence/')
        ? new Response(JSON.stringify(evidence), {
            headers: { 'Content-Type': 'application/json' },
          })
        : new Response(
            JSON.stringify({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 }),
            { headers: { 'Content-Type': 'application/json' } },
          ),
    )
    vi.stubGlobal('fetch', fetchMock)
    await expect(fetchCoverageRequests()).resolves.toMatchObject({ totalCount: 0 })
    await expect(fetchModerationEvidence(evidence.scopeId, 'DECLARATION')).resolves.toMatchObject({
      content: 'safe text',
    })
    expect(fetchMock.mock.calls[1]?.[1]).toMatchObject({ cache: 'no-store' })
  })

  it('maps security, stale, authorization and audit failures to English messages', () => {
    expect(
      moderationErrorMessage(
        new ModerationApiError(403, 'Forbidden', 'ERR_EVIDENCE_SECURITY_RESTRICTED'),
      ),
    ).toContain('security-restricted')
    expect(
      moderationErrorMessage(
        new ModerationApiError(409, 'Conflict', 'ERR_MODERATION_APPEAL_DECISION_NOT_ALLOWED'),
      ),
    ).toContain('not allowed for an appeal')
    expect(
      moderationErrorMessage(new ModerationApiError(403, 'Forbidden', 'ERR_MODERATION_NOT_HOLDER')),
    ).toContain('Claim this case')
    expect(
      moderationErrorMessage(
        new ModerationApiError(503, 'Unavailable', 'ERR_EVIDENCE_AUDIT_UNAVAILABLE'),
      ),
    ).toContain('No content was released')
  })

  it('maps claim and release conflicts without claiming that decision text was preserved', () => {
    const conflict = new ModerationApiError(409, 'Conflict', 'ERR_MODERATION_CASE_STALE')
    expect(moderationErrorMessage(conflict, 'claim')).toContain('Claim state changed')
    expect(moderationErrorMessage(conflict, 'claim')).not.toContain('text is preserved')
    expect(moderationErrorMessage(conflict, 'release')).toContain('Release state changed')
  })

  it('uses shared endpoint paths for coverage queue, case evidence, and assessment', async () => {
    const oldQueue = moderationBackendEndpoints.coverageQueue.path
    const oldEvidence = moderationBackendEndpoints.caseEvidence.path
    const oldAssessment = moderationBackendEndpoints.coverageAssessment.path
    moderationBackendEndpoints.coverageQueue.path = '/shared/coverage-queue'
    moderationBackendEndpoints.caseEvidence.path = '/shared/cases/{id}/evidence/{kind}'
    moderationBackendEndpoints.coverageAssessment.path = '/shared/coverage/{id}/assess'
    const jsonResponse = (value: unknown) =>
      new Response(JSON.stringify(value), {
        headers: { 'Content-Type': 'application/json' },
      })
    const fetchMock = vi.fn(async (input: RequestInfo | URL) =>
      String(input).includes('/evidence/')
        ? jsonResponse({
            scopeId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
            reportId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
            kind: 'DECLARATION',
            findingId: null,
            content: '',
            itemCount: 0,
            truncated: false,
          })
        : String(input).includes('/assess')
          ? jsonResponse({
              requestId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
              requestRevision: 2,
              workspaceRevision: 3,
              tupleDigest: 'a'.repeat(64),
              replayed: false,
            })
          : jsonResponse({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 }),
    )
    vi.stubGlobal('fetch', fetchMock)
    try {
      await fetchCoverageRequests()
      await fetchModerationEvidence('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'DECLARATION')
      await postCoverageAssessment('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', {
        operationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
        expectedRequestRevision: 1,
        expectedTupleDigest: 'a'.repeat(64),
        outcome: 'BLOCKED',
        competence: ['Review'],
        scope: [],
        manageableScope: false,
        reviewPlan: 'No readable scope.',
        completedPlanReference: '',
        rationale: 'Cannot inspect evidence.',
      })
      expect(
        fetchMock.mock.calls.some((call) => String(call[0]).startsWith('/shared/coverage/')),
      ).toBe(true)
      expect(fetchMock.mock.calls.map((call) => String(call[0]))).toContain(
        '/shared/coverage-queue?page=1',
      )
      expect(
        fetchMock.mock.calls
          .map((call) => String(call[0]))
          .some((path) => path.startsWith('/shared/cases/')),
      ).toBe(true)
    } finally {
      moderationBackendEndpoints.coverageQueue.path = oldQueue
      moderationBackendEndpoints.caseEvidence.path = oldEvidence
      moderationBackendEndpoints.coverageAssessment.path = oldAssessment
    }
  })
})
