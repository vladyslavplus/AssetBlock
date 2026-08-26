import { QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { AssetEditPageClient } from '@/components/sell/asset-edit-page-client'
import type { SellerAssetDetail } from '@/lib/seller/seller-asset-schemas'
import { createTestQueryClient } from '@/test/query-client'
import { verifiedSeller } from '@/test/session-user'

const useAuth = vi.hoisted(() => vi.fn())

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn(), replace: vi.fn() }),
  usePathname: () => '/sell/assets/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/edit',
  useSearchParams: () => new URLSearchParams(),
}))

vi.mock('@/components/auth/auth-context', () => ({
  useAuth: () => useAuth(),
}))

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }))

const pendingAsset: SellerAssetDetail = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  title: 'Pending Pack',
  description: null,
  price: 12,
  categoryId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  categoryName: '3D',
  authorId: '11111111-1111-4111-8111-111111111111',
  authorUsername: 'seller',
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: null,
  tags: [],
  latestVersionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  latestVersionNumber: 1,
  currentReadyVersionId: null,
  latestProcessingStatus: 'PENDING_INSPECTION',
  latestProcessingUpdatedAt: '2026-01-01T00:00:00.000Z',
  latestProcessingErrorCode: null,
  latestProcessingErrorSummary: null,
}

describe('AssetEditPageClient', () => {
  beforeEach(() => {
    useAuth.mockReturnValue({
      user: verifiedSeller(),
      status: 'authenticated',
      isAdmin: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders a pending owned listing with processing status instead of 404', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url.includes('/api/categories') || url.includes('/api/tags')) {
          return new Response(
            JSON.stringify({ items: [], totalCount: 0, page: 1, pageSize: 100 }),
            {
              status: 200,
              headers: { 'Content-Type': 'application/json' },
            },
          )
        }
        if (url.includes('/versions') || url.includes('processing-jobs')) {
          return new Response(JSON.stringify([]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        }
        throw new Error(`unexpected fetch ${url}`)
      }),
    )

    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetEditPageClient assetId={pendingAsset.id} initialAsset={pendingAsset} />
      </QueryClientProvider>,
    )

    expect(await screen.findByText('Inspecting archive')).toBeInTheDocument()
    expect(
      screen.getByText(/being inspected before it can appear in the catalog/i),
    ).toBeInTheDocument()
    expect(screen.queryByText(/not found/i)).not.toBeInTheDocument()
  })
})
