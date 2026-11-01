import { fireEvent, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SellerAssetVersionsSection } from './seller-asset-versions-section'
import { renderWithQueryClient } from '@/test/render'

vi.mock('@/components/sell/asset-processing-status-panel', () => ({
  AssetProcessingStatusPanel: () => null,
}))
vi.mock('@/components/sell/publish-version-form', () => ({
  PublishVersionForm: () => null,
}))
vi.mock('@/components/sell/source-declaration-editor', () => ({
  SourceDeclarationEditor: () => null,
}))

const assetId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const publishedVersion = '11111111-1111-4111-8111-111111111111'
const pendingVersion = '22222222-2222-4222-8222-222222222222'

function version(id: string, versionNumber: number) {
  return {
    id,
    versionNumber,
    isCurrent: versionNumber === 2,
    fileName: `package-v${versionNumber}.zip`,
    contentLength: 1,
    contentSha256: 'a'.repeat(64),
    releaseNotes: '',
    createdAt: '2026-10-31T10:00:00Z',
    license: { displayName: 'Personal' },
    processingStatus: 'READY',
  }
}

function review(id: string, versionNumber: number, state: string, published: boolean) {
  return {
    coverageReview: null,
    requiredResponseCount: 0,
    outstandingResponseCount: 0,
    evidenceRevision: 0,
    assetId,
    assetVersionId: id,
    versionNumber,
    processingStatus: 'READY',
    processingErrorCode: null,
    processingErrorSummary: null,
    analysis: 'ANALYSIS_NOT_AVAILABLE',
    moderationState: state,
    publicationEligible: published,
    canUpload: true,
    canSubmit: false,
    canAppeal: false,
    appeal: null,
    activeSubmission: null,
    history: [],
    blockedReasons: [],
    decision: published
      ? {
          outcome: 'APPROVED',
          reason: null,
          decidedAt: '2026-10-31T10:00:00Z',
          publication: 'PUBLISHED',
        }
      : null,
  }
}

describe('seller asset version review states', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('keeps approved v1 published while pending v2 review is opened beside it', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url.includes(`/versions/${publishedVersion}/review`))
          return new Response(JSON.stringify(review(publishedVersion, 1, 'APPROVED', true)))
        if (url.includes(`/versions/${pendingVersion}/review`))
          return new Response(JSON.stringify(review(pendingVersion, 2, 'IN_REVIEW', false)))
        return new Response(
          JSON.stringify([version(pendingVersion, 2), version(publishedVersion, 1)]),
        )
      }),
    )
    renderWithQueryClient(<SellerAssetVersionsSection assetId={assetId} />)

    fireEvent.click(await screen.findByRole('button', { name: 'View checks for version 1' }))
    fireEvent.click(await screen.findByRole('button', { name: 'View checks for version 2' }))

    expect(
      await screen.findByRole('button', { name: 'Hide checks for version 1' }),
    ).toBeInTheDocument()
    expect(
      await screen.findByRole('button', { name: 'Hide checks for version 2' }),
    ).toBeInTheDocument()
    expect(await screen.findByText(/Publication: Published/)).toBeInTheDocument()
    expect(screen.getByText('Publicly listed')).toBeInTheDocument()
    expect(screen.getByText('Candidate — not published')).toBeInTheDocument()
    expect(screen.getAllByText('Review in progress')).toHaveLength(2)
  })
})
