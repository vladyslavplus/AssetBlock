import { screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { VersionChecksPanel } from '@/components/sell/version-checks-panel'
import { renderWithQueryClient } from '@/test/render'

const UUID_ASSET = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const UUID_VERSION = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'

function stubReviewFetch(review: Record<string, unknown>) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify(review), { status: 200 })),
  )
}

function review(overrides: Record<string, unknown> = {}) {
  return {
    assetId: UUID_ASSET,
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
})
