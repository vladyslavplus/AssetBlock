import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { SellMyBundles } from '@/components/sell/sell-my-bundles'
import { renderWithQueryClient } from '@/test/render'
import { verifiedSeller } from '@/test/session-user'

const useAuth = vi.hoisted(() => vi.fn())

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn(), replace: vi.fn() }),
  usePathname: () => '/sell',
  useSearchParams: () => new URLSearchParams('tab=bundles'),
}))

vi.mock('@/components/auth/auth-context', () => ({
  useAuth: () => useAuth(),
}))

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }))

function authSeller() {
  useAuth.mockReturnValue({
    user: verifiedSeller(),
    status: 'authenticated',
    isAdmin: false,
    refresh: vi.fn(),
    logout: vi.fn(),
  })
}

describe('SellMyBundles', () => {
  it('does not mask a listings failure as an empty asset picker', async () => {
    authSeller()
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url.includes('/api/seller/bundles')) {
          return new Response(JSON.stringify({ items: [], totalCount: 0, page: 1, pageSize: 50 }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        }
        if (url.includes('/api/seller/listings')) {
          return new Response(JSON.stringify({ title: 'Server error' }), { status: 500 })
        }
        return new Response('{}', { status: 200 })
      }),
    )
    renderWithQueryClient(<SellMyBundles />)
    expect(await screen.findByText(/could not load your assets/i)).toBeInTheDocument()
    expect(screen.queryByText(/no bundles yet/i)).toBeInTheDocument()
  })

  it('blocks invalid create submit without calling the API', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.includes('/api/seller/listings')) {
        return new Response(JSON.stringify({ items: [], totalCount: 0, page: 1, pageSize: 50 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (url.includes('/api/seller/bundles') && (!init?.method || init.method === 'GET')) {
        return new Response(JSON.stringify({ items: [], totalCount: 0, page: 1, pageSize: 50 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      throw new Error(`Unexpected request ${init?.method ?? 'GET'} ${url}`)
    })
    authSeller()
    vi.stubGlobal('fetch', fetchMock)
    renderWithQueryClient(<SellMyBundles />)
    await screen.findByText(/no bundles yet/i)
    await user.click(screen.getByRole('button', { name: /create bundle/i }))
    expect(await screen.findByText(/title is required/i)).toBeInTheDocument()
    expect(fetchMock.mock.calls.some((call) => String(call[1]?.method ?? 'GET') !== 'GET')).toBe(
      false,
    )
  })
})
