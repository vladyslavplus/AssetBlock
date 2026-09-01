import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { SellMyCollections } from '@/components/sell/sell-my-collections'
import { renderWithQueryClient } from '@/test/render'
import { verifiedSeller } from '@/test/session-user'

const useAuth = vi.hoisted(() => vi.fn())

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn(), replace: vi.fn() }),
  usePathname: () => '/sell',
  useSearchParams: () => new URLSearchParams('tab=collections'),
}))

vi.mock('@/components/auth/auth-context', () => ({
  useAuth: () => useAuth(),
}))

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }))

const collectionId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'
const sellerId = '11111111-1111-4111-8111-111111111111'

const collectionListItem = {
  id: collectionId,
  title: 'Starter pack',
  description: null,
  status: 'DRAFT',
  publishedAt: null,
  createdAt: '2026-01-01T00:00:00.000Z',
  sellerId,
  sellerUsername: 'seller',
  itemCount: 0,
  coverAssetId: null,
  coverAssetTitle: null,
}

const collectionDetail = {
  ...collectionListItem,
  archivedAt: null,
  updatedAt: '2026-01-01T00:00:00.000Z',
  items: [],
}

describe('SellMyCollections listings error', () => {
  it('does not show the empty-listings upload prompt when listings fail', async () => {
    const user = userEvent.setup()
    useAuth.mockReturnValue({
      user: verifiedSeller(),
      status: 'authenticated',
      isAdmin: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url.includes(`/api/seller/collections/${collectionId}`)) {
          return new Response(JSON.stringify(collectionDetail), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        }
        if (url.includes('/api/seller/collections')) {
          return new Response(
            JSON.stringify({
              items: [collectionListItem],
              totalCount: 1,
              page: 1,
              pageSize: 50,
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          )
        }
        if (url.includes('/api/seller/listings')) {
          return new Response(JSON.stringify({ title: 'Server error' }), { status: 500 })
        }
        return new Response('{}', { status: 200 })
      }),
    )
    renderWithQueryClient(<SellMyCollections />)
    await user.click(await screen.findByRole('button', { name: /starter pack/i }))
    expect(await screen.findByText(/could not load your assets/i)).toBeInTheDocument()
    expect(screen.queryByText(/no available assets to add/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/upload assets first/i)).not.toBeInTheDocument()
  })
})
