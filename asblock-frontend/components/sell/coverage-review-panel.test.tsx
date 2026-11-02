import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { CoverageReviewPanel } from './coverage-review-panel'
import { renderWithQueryClient } from '@/test/render'
import { sellerVersionReviewSchema } from '@/lib/seller/seller-draft-schemas'

const id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const version = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const report = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const review = sellerVersionReviewSchema.parse({
  assetId: id,
  assetVersionId: version,
  versionNumber: 1,
  processingStatus: 'READY',
  processingErrorCode: null,
  processingErrorSummary: null,
  analysis: 'ANALYSIS_AVAILABLE',
  moderationState: 'DRAFT',
  publicationEligible: false,
  canUpload: true,
  canSubmit: false,
  canAppeal: false,
  appeal: null,
  activeSubmission: null,
  history: [],
  blockedReasons: ['ERR_REVIEW_COVERAGE_REQUIRED'],
  requiredResponseCount: 0,
  outstandingResponseCount: 0,
  evidenceRevision: 0,
  coverageReview: {
    requestId: null,
    status: 'NOT_REQUESTED',
    reasonCode: 'ERR_REVIEW_COVERAGE_REQUIRED',
    canRequest: true,
  },
  analysisDetails: {
    assetId: id,
    assetVersionId: version,
    runId: id,
    reportId: report,
    reportIsCurrent: true,
    lifecycle: 'COMPLETED',
    coverageLevel: 'PARTIAL',
    analyzedFileCount: 1,
    omissionCount: 1,
    findingCount: 0,
    findingDetailsAvailable: true,
    createdAt: null,
    completedAt: null,
    errorCode: null,
    errorMessage: null,
    actionHint: null,
    canRetry: false,
    retryExpectation: null,
    canSubmit: false,
    reviewEligible: true,
    submissionBlockedReason: 'ERR_REVIEW_COVERAGE_REQUIRED',
    comparisonScope: 'Compared source only',
    limitations: [],
    stages: [],
    requiredResponseCount: 0,
    outstandingResponseCount: 0,
    evidenceRevision: 0,
    coverageReview: null,
  },
})
const analysisDetails = review.analysisDetails
if (!analysisDetails) throw new Error('Expected analysis details')
const page = {
  workspaceId: id,
  workspaceRevision: 2,
  caseRevision: 1,
  evidenceRevision: 0,
  reportId: report,
  requiredCount: 0,
  outstandingCount: 0,
  evidenceText: '',
  items: [],
  totalCount: 0,
  page: 1,
  pageSize: 50,
  totalPages: 0,
}
describe('coverage review status and request', () => {
  afterEach(() => vi.unstubAllGlobals())
  it.each([null, 'FULL'] as const)('hides when coverage is %s', (coverageLevel) => {
    renderWithQueryClient(
      <CoverageReviewPanel
        assetId={id}
        versionId={version}
        review={{ ...review, analysisDetails: { ...analysisDetails, coverageLevel } }}
      />,
    )
    expect(screen.queryByLabelText('Coverage review')).not.toBeInTheDocument()
  })
  it('hides for a report that is not current', () => {
    renderWithQueryClient(
      <CoverageReviewPanel
        assetId={id}
        versionId={version}
        review={{ ...review, analysisDetails: { ...analysisDetails, reportIsCurrent: false } }}
      />,
    )
    expect(screen.queryByLabelText('Coverage review')).not.toBeInTheDocument()
  })
  it('replays the original request after uncertain transport and never suggests publication approval', async () => {
    const bodies: unknown[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          bodies.push(JSON.parse(init.body as string))
          if (bodies.length === 1) throw new TypeError('Connection lost')
          return new Response(
            JSON.stringify({
              requestId: id,
              requestRevision: 1,
              workspaceRevision: 3,
              tupleDigest: 'a'.repeat(64),
              replayed: true,
            }),
          )
        }
        return new Response(JSON.stringify(page))
      }),
    )
    renderWithQueryClient(<CoverageReviewPanel assetId={id} versionId={version} review={review} />)
    const button = await screen.findByRole('button', { name: 'Request coverage review' })
    await waitFor(() => expect(button).not.toBeDisabled())
    fireEvent.click(button)
    await screen.findByText(/Result unknown/)
    fireEvent.click(screen.getByRole('button', { name: 'Retry same coverage request' }))
    await screen.findByText('Coverage review requested.')
    expect(bodies).toHaveLength(2)
    expect(bodies[0]).toEqual(bodies[1])
    expect(bodies[0]).toMatchObject({
      workspaceId: id,
      reportId: report,
      expectedWorkspaceRevision: 2,
    })
    expect(screen.getByText(/does not approve publication/)).toBeInTheDocument()
  })
  it('shows invalidation reason without Moderator rationale or a request button when unavailable', () => {
    renderWithQueryClient(
      <CoverageReviewPanel
        assetId={id}
        versionId={version}
        review={{
          ...review,
          coverageReview: {
            requestId: id,
            status: 'INVALIDATED',
            reasonCode: 'ERR_COVERAGE_ASSESSMENT_INVALIDATED',
            canRequest: false,
          },
        }}
      />,
    )
    expect(screen.getByText(/no longer matches this version/)).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})
