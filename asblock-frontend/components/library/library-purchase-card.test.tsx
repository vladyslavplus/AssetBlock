import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { LibraryPurchaseCard } from '@/components/library/library-purchase-card'
import type { PurchaseLibraryItem } from '@/lib/library/purchase-types'
import { renderWithQueryClient } from '@/test/render'

vi.mock('@/components/reviews/leave-review-dialog', () => ({
  LeaveReviewDialog: () => null,
}))
vi.mock('@/lib/analytics/telemetry-client', () => ({ trackAnalyticsEvent: vi.fn() }))

const purchase: PurchaseLibraryItem = {
  id: '11111111-1111-4111-8111-111111111111',
  orderId: '22222222-2222-4222-8222-222222222222',
  assetId: '33333333-3333-4333-8333-333333333333',
  assetTitle: 'Terrain kit',
  price: 20,
  purchasedAt: '2026-08-20T00:00:00.000Z',
  authorUsername: 'maker',
  hasUserReviewed: true,
  purchasedVersionNumber: 1,
  purchasedVersionId: '44444444-4444-4444-8444-444444444444',
  latestEntitledVersionNumber: 2,
  latestEntitledVersionId: '55555555-5555-4555-8555-555555555555',
  hasUpdate: true,
  pricePaid: 20,
  currency: 'usd',
  source: 'ASSET',
  bundleId: null,
  bundleTitle: null,
}

afterEach(() => vi.unstubAllGlobals())

describe('LibraryPurchaseCard', () => {
  it('defaults to latest entitled version and changes download URL when version changes', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    renderWithQueryClient(<LibraryPurchaseCard purchase={purchase} />)

    const download = screen.getByRole('link', { name: /download/i })
    expect(download).toHaveAttribute(
      'href',
      `/api/assets/${purchase.assetId}/download?versionId=${purchase.latestEntitledVersionId}`,
    )
    await userEvent.selectOptions(
      screen.getByLabelText('Download version'),
      purchase.purchasedVersionId,
    )
    expect(download).toHaveAttribute(
      'href',
      `/api/assets/${purchase.assetId}/download?versionId=${purchase.purchasedVersionId}`,
    )
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('loads version history only when expanded and shows unavailable processing state safely', async () => {
    const fetchMock = vi.fn(async () =>
      Response.json([
        {
          id: purchase.latestEntitledVersionId,
          versionNumber: 2,
          isCurrent: true,
          fileName: 'terrain.zip',
          contentLength: 1024,
          contentSha256: 'abc123',
          releaseNotes: null,
          createdAt: '2026-08-20T00:00:00.000Z',
          license: { code: 'STANDARD', displayName: 'Standard' },
          processingStatus: 'PENDING_MALWARE_SCAN',
          processingErrorCode: null,
          processingErrorSummary: null,
          processingUpdatedAt: '2026-08-20T00:00:00.000Z',
        },
      ]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderWithQueryClient(<LibraryPurchaseCard purchase={purchase} />)
    expect(fetchMock).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: 'Version history' }))
    expect(await screen.findByText('v2')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledOnce()
  })

  it('shows a stable failure state when version history request fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Response.json({ title: 'Failure' }, { status: 500 })),
    )
    renderWithQueryClient(<LibraryPurchaseCard purchase={purchase} />)
    await userEvent.click(screen.getByRole('button', { name: 'Version history' }))
    await waitFor(() =>
      expect(screen.getByText('Version history is unavailable.')).toBeInTheDocument(),
    )
  })
})
