import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { SellMyListings } from '@/components/sell/sell-my-listings'
import type * as sellerApi from '@/lib/seller/seller-api'
import { sellerKeys } from '@/lib/seller/seller-query'
import { renderWithQueryClient } from '@/test/render'
import { verifiedSeller } from '@/test/session-user'

const deleteSellerAsset = vi.hoisted(() => vi.fn())
const useAuth = vi.hoisted(() => vi.fn())

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn(), replace: vi.fn() }),
  usePathname: () => '/sell',
  useSearchParams: () => new URLSearchParams(),
}))

vi.mock('@/components/auth/auth-context', () => ({
  useAuth: () => useAuth(),
}))

vi.mock('@/lib/seller/seller-api', async () => {
  const actual = await vi.importActual<typeof sellerApi>('@/lib/seller/seller-api')
  return { ...actual, deleteSellerAsset }
})

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }))

const listing = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  title: 'Forest Pack',
  description: null,
  price: 15,
  categoryId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  categoryName: '3D',
  authorId: '11111111-1111-4111-8111-111111111111',
  authorUsername: 'seller',
  createdAt: '2026-01-01T00:00:00.000Z',
  tags: [],
  averageRating: 0,
  latestVersionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  latestVersionNumber: 1,
  currentReadyVersionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  latestProcessingStatus: 'READY',
  latestProcessingUpdatedAt: '2026-01-01T00:00:00.000Z',
  latestProcessingErrorCode: null,
  latestProcessingErrorSummary: null,
}

const pendingListing = {
  ...listing,
  id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  title: 'Pending Pack',
  currentReadyVersionId: null,
  latestProcessingStatus: 'PENDING_INSPECTION',
}

describe('SellMyListings', () => {
  beforeEach(() => {
    useAuth.mockReturnValue({
      user: verifiedSeller(),
      status: 'authenticated',
      isAdmin: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
  })

  it('shows a signed-out prompt', async () => {
    useAuth.mockReturnValue({
      user: null,
      status: 'anonymous',
      isAdmin: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
    renderWithQueryClient(<SellMyListings />)
    expect(await screen.findByText(/sign in to see assets you have published/i)).toBeInTheDocument()
  })

  it('shows a retryable error instead of raw internals', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('{"type":"urn:assetblock:error:ERR_X"}', { status: 500 })),
    )
    renderWithQueryClient(<SellMyListings />)
    expect(await screen.findByText(/could not load listings/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument()
    expect(screen.queryByText(/ZodError|ERR_X|stack/)).not.toBeInTheDocument()
  })

  it('confirms delete, tracks pending target, and invalidates the list', async () => {
    const user = userEvent.setup()
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        return new Response(
          JSON.stringify({ items: [listing], totalCount: 1, page: 1, pageSize: 50 }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        )
      }),
    )
    let resolveDelete: (value: unknown) => void = () => {}
    deleteSellerAsset.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveDelete = resolve
        }),
    )
    const { queryClient } = renderWithQueryClient(<SellMyListings />)
    expect(await screen.findByText('Forest Pack')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /delete/i }))
    expect(await screen.findByText(/delete this listing/i)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /delete permanently/i }))
    expect(await screen.findByText(/deleting/i)).toBeInTheDocument()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    resolveDelete({ ok: true })
    await waitFor(() => {
      expect(invalidate).toHaveBeenCalledWith(
        expect.objectContaining({ queryKey: sellerKeys.all }),
        expect.anything(),
      )
    })
  })

  it('shows View for a READY listing and Manage for a pending listing', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        return new Response(
          JSON.stringify({
            items: [listing, pendingListing],
            totalCount: 2,
            page: 1,
            pageSize: 50,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        )
      }),
    )
    renderWithQueryClient(<SellMyListings />)
    expect(await screen.findByText('Forest Pack')).toBeInTheDocument()
    expect(screen.getByText('Pending Pack')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /view/i })).toHaveAttribute(
      'href',
      '/assets/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    )
    expect(screen.getByRole('link', { name: /manage/i })).toHaveAttribute(
      'href',
      '/sell/assets/dddddddd-dddd-4ddd-8ddd-dddddddddddd/edit',
    )
    expect(screen.getByText('Live')).toBeInTheDocument()
    expect(screen.getByText('Inspecting archive')).toBeInTheDocument()
  })
})
