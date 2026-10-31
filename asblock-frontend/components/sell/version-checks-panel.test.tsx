import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { VersionChecksPanel } from '@/components/sell/version-checks-panel'
import { sellerKeys } from '@/lib/seller/seller-query'
import type { SellerAnalysisFindingsPage } from '@/lib/seller/seller-draft-schemas'
import { renderWithQueryClient } from '@/test/render'

const UUID_ASSET = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const UUID_VERSION = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const UUID_RUN = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'
const UUID_REPORT = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'

function analysisDetails(overrides: Record<string, unknown> = {}) {
  return {
    assetId: UUID_ASSET,
    requiredResponseCount: 0,
    outstandingResponseCount: 0,
    evidenceRevision: 0,
    assetVersionId: UUID_VERSION,
    lifecycle: 'FAILED',
    runId: UUID_RUN,
    reportId: null,
    reportIsCurrent: false,
    coverageLevel: null,
    analyzedFileCount: 0,
    omissionCount: 0,
    findingCount: 0,
    findingDetailsAvailable: true,
    createdAt: null,
    completedAt: null,
    errorCode: 'TOKENIZER_UNAVAILABLE',
    errorMessage:
      'Analysis could not complete. Review the current version details before retrying.',
    actionHint: 'Retry analysis after checking the current seller declaration and materials.',
    canRetry: true,
    retryExpectation: {
      runId: UUID_RUN,
      workspaceRevision: 3,
      policyDigest: 'a'.repeat(64),
      inputDigest: 'b'.repeat(64),
    },
    canSubmit: false,
    reviewEligible: false,
    submissionBlockedReason: 'ERR_ANALYSIS_NOT_AVAILABLE',
    comparisonScope: 'Selected source files from this version only',
    limitations: ['Research evaluation only.'],
    stages: [],
    ...overrides,
  }
}

function stubReviewFetch(review: Record<string, unknown>) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify(review), { status: 200 })),
  )
}

function review(overrides: Record<string, unknown> = {}) {
  return {
    assetId: UUID_ASSET,
    requiredResponseCount: 0,
    outstandingResponseCount: 0,
    evidenceRevision: 0,
    assetVersionId: UUID_VERSION,
    versionNumber: 2,
    processingStatus: 'READY',
    processingErrorCode: null,
    processingErrorSummary: null,
    analysis: 'ANALYSIS_NOT_AVAILABLE',
    moderationState: 'DRAFT',
    publicationEligible: false,
    canUpload: true,
    canSubmit: false,
    blockedReasons: ['ERR_ANALYSIS_NOT_AVAILABLE', 'ERR_DECLARATION_INCOMPLETE'],
    ...overrides,
  }
}

function renderPanel() {
  return renderWithQueryClient(<VersionChecksPanel assetId={UUID_ASSET} versionId={UUID_VERSION} />)
}

describe('VersionChecksPanel', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('shows READY as technical completion, never as publication', async () => {
    stubReviewFetch(review())
    renderPanel()

    await waitFor(() => {
      expect(screen.getByText(/file checks:/i)).toBeInTheDocument()
    })
    expect(screen.getByText('File checks passed')).toBeInTheDocument()
    expect(screen.getByText('Candidate — not published')).toBeInTheDocument()
    expect(screen.getByText(/publication needs an approved review/i)).toBeInTheDocument()
    expect(screen.queryByText(/publicly listed/i)).not.toBeInTheDocument()
  })

  it('honestly reports unavailable analysis with the canonical blocked reasons', async () => {
    stubReviewFetch(review())
    renderPanel()

    await waitFor(() => {
      expect(
        screen.getByText(/code analysis is not available yet for this version\./i),
      ).toBeInTheDocument()
    })
    expect(
      screen.getByText(/the source declaration is incomplete for this version\./i),
    ).toBeInTheDocument()
    expect(screen.getByText(/submission is not available yet\./i)).toBeInTheDocument()
  })

  it('shows an approved version as publicly listed without fake queue actions', async () => {
    stubReviewFetch(
      review({
        publicationEligible: true,
        canSubmit: true,
        blockedReasons: [],
        moderationState: 'APPROVED',
      }),
    )
    renderPanel()

    await waitFor(() => {
      expect(screen.getByText('Publicly listed')).toBeInTheDocument()
    })
    expect(screen.getByText(/this version is eligible for submission/i)).toBeInTheDocument()
  })

  it('distinguishes a failed security check from moderation rejection', async () => {
    stubReviewFetch(
      review({
        processingStatus: 'REJECTED',
        processingErrorSummary: 'Malware signature detected',
        moderationState: 'DRAFT',
      }),
    )
    renderPanel()

    await waitFor(() => {
      expect(screen.getByText(/security checks failed/i)).toBeInTheDocument()
    })
    expect(screen.getByText(/malware signature detected/i)).toBeInTheDocument()
  })

  it('keeps accepted retry visible when refreshed review says retry is no longer available', async () => {
    let reviewReads = 0
    vi.stubGlobal('crypto', { randomUUID: () => 'ffffffff-ffff-4fff-8fff-ffffffffffff' })
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        if (url.includes('analysis-retries') && init?.method === 'POST') {
          return new Response(
            JSON.stringify({
              operationId: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
              assetId: UUID_ASSET,
              requiredResponseCount: 0,
              outstandingResponseCount: 0,
              evidenceRevision: 0,
              assetVersionId: UUID_VERSION,
              status: 'ACCEPTED',
              runId: UUID_RUN,
              jobId: '99999999-9999-4999-8999-999999999999',
              securityRecheckId: null,
              replayed: false,
            }),
            { status: 202 },
          )
        }
        if (url.endsWith('/review')) {
          reviewReads += 1
          return new Response(
            JSON.stringify(
              review({
                analysisDetails: analysisDetails(
                  reviewReads > 1
                    ? { lifecycle: 'QUEUED', canRetry: false, retryExpectation: null }
                    : {},
                ),
              }),
            ),
            { status: 200 },
          )
        }
        return new Response('{}', { status: 404 })
      }),
    )
    renderPanel()

    fireEvent.click(await screen.findByRole('button', { name: 'Retry analysis' }))
    await waitFor(() =>
      expect(screen.getByText(/retry accepted\. status will update/i)).toBeInTheDocument(),
    )
    await waitFor(() => expect(reviewReads).toBeGreaterThan(1))
    expect(screen.queryByRole('button', { name: 'Retry analysis' })).not.toBeInTheDocument()
    expect(screen.getByText(/retry accepted\. status will update/i)).toBeInTheDocument()
  })

  it('shows an honest empty findings result for an admitted report', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url.endsWith('/review')) {
          return new Response(
            JSON.stringify(
              review({
                analysis: 'ANALYSIS_AVAILABLE',
                analysisDetails: analysisDetails({
                  lifecycle: 'COMPLETED',
                  runId: UUID_RUN,
                  reportId: UUID_REPORT,
                  reportIsCurrent: true,
                  coverageLevel: 'PARTIAL',
                  analyzedFileCount: 2,
                  omissionCount: 1,
                  canRetry: false,
                  retryExpectation: null,
                  submissionBlockedReason: 'ERR_REVIEW_COVERAGE_REQUIRED',
                }),
              }),
            ),
            { status: 200 },
          )
        }
        if (url.includes('/findings')) {
          return new Response(
            JSON.stringify({
              assetId: UUID_ASSET,
              requiredResponseCount: 0,
              outstandingResponseCount: 0,
              evidenceRevision: 0,
              assetVersionId: UUID_VERSION,
              reportId: UUID_REPORT,
              isCurrent: true,
              coverageLevel: 'PARTIAL',
              items: [],
              totalCount: 0,
              detailsAvailable: true,
              page: 1,
              pageSize: 50,
              totalPages: 0,
            }),
            { status: 200 },
          )
        }
        return new Response('{}', { status: 404 })
      }),
    )
    renderPanel()

    expect(
      await screen.findByText(/no findings were recorded for this compared scope/i),
    ).toBeInTheDocument()
    expect(screen.getByText(/partial coverage/i)).toBeInTheDocument()
  })

  it('shows explicit historical detail expiry while retaining summary counts', async () => {
    stubReviewFetch(
      review({
        analysis: 'ANALYSIS_AVAILABLE',
        analysisDetails: analysisDetails({
          lifecycle: 'COMPLETED',
          reportId: UUID_REPORT,
          coverageLevel: 'PARTIAL',
          findingCount: 4,
          findingDetailsAvailable: false,
          canRetry: false,
          retryExpectation: null,
        }),
      }),
    )
    renderPanel()

    expect(
      await screen.findByText(/historical finding details are no longer available/i),
    ).toBeInTheDocument()
    expect(screen.getByText(/4 findings/)).toBeInTheDocument()
  })

  it('evicts cached excerpts when refreshed summary marks historical detail unavailable', async () => {
    const { QueryClient } = await import('@tanstack/react-query')
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const cachedPage: SellerAnalysisFindingsPage = {
      assetId: UUID_ASSET,
      assetVersionId: UUID_VERSION,
      reportId: UUID_REPORT,
      isCurrent: false,
      coverageLevel: 'PARTIAL',
      items: [
        {
          id: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
          findingType: 'EXACT_MATCH',
          ownPath: 'src/private.ts',
          ownExcerpt: 'cached private excerpt',
          startByte: 0,
          endByte: 20,
          startLine: 1,
          endLine: 1,
          reason: 'Matched selected code.',
          requestedAction: 'Review the source.',
          responseRequired: false,
          hasResponse: false,
          referenceKind: 'FOREIGN_PRIVATE',
          publicAttribution: null,
        },
      ],
      totalCount: 4,
      detailsAvailable: true,
      page: 1,
      pageSize: 50,
      totalPages: 2,
    }
    queryClient.setQueryData(
      sellerKeys.analysisFindings(UUID_ASSET, UUID_VERSION, UUID_REPORT, 1),
      cachedPage,
    )
    stubReviewFetch(
      review({
        analysis: 'ANALYSIS_AVAILABLE',
        analysisDetails: analysisDetails({
          lifecycle: 'COMPLETED',
          reportId: UUID_REPORT,
          findingCount: 4,
          findingDetailsAvailable: false,
          canRetry: false,
          retryExpectation: null,
        }),
      }),
    )
    renderWithQueryClient(<VersionChecksPanel assetId={UUID_ASSET} versionId={UUID_VERSION} />, {
      queryClient,
    })

    expect(
      await screen.findByText(/historical finding details are no longer available/i),
    ).toBeInTheDocument()
    expect(screen.getByText(/4 findings/)).toBeInTheDocument()
    expect(screen.queryByText('cached private excerpt')).not.toBeInTheDocument()
    expect(screen.queryByText(/page 1 of 2/i)).not.toBeInTheDocument()
    await waitFor(() =>
      expect(
        queryClient.getQueryData(
          sellerKeys.analysisFindings(UUID_ASSET, UUID_VERSION, UUID_REPORT, 1),
        ),
      ).toBeUndefined(),
    )
    queryClient.clear()
  })

  it('shows unavailable detail when the findings page expires after an available summary', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url.endsWith('/review')) {
          return new Response(
            JSON.stringify(
              review({
                analysis: 'ANALYSIS_AVAILABLE',
                analysisDetails: analysisDetails({
                  lifecycle: 'COMPLETED',
                  reportId: UUID_REPORT,
                  findingCount: 3,
                  findingDetailsAvailable: true,
                  canRetry: false,
                  retryExpectation: null,
                }),
              }),
            ),
            { status: 200 },
          )
        }
        if (url.includes('/findings')) {
          return new Response(
            JSON.stringify({
              assetId: UUID_ASSET,
              requiredResponseCount: 0,
              outstandingResponseCount: 0,
              evidenceRevision: 0,
              assetVersionId: UUID_VERSION,
              reportId: UUID_REPORT,
              isCurrent: false,
              coverageLevel: 'PARTIAL',
              items: [],
              totalCount: 3,
              detailsAvailable: false,
              page: 1,
              pageSize: 50,
              totalPages: 0,
            }),
            { status: 200 },
          )
        }
        return new Response('{}', { status: 404 })
      }),
    )
    renderPanel()

    expect(
      await screen.findByText(/historical finding details are no longer available/i),
    ).toBeInTheDocument()
    expect(screen.getByText(/3 findings/)).toBeInTheDocument()
    expect(screen.queryByText(/no findings were recorded/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/page 1 of/i)).not.toBeInTheDocument()
  })
})
