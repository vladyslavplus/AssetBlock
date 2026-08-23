import { QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { AssetPurchaseCard } from '@/components/assets/asset-purchase-card'
import { createTestQueryClient } from '@/test/query-client'
import { verifiedSeller } from '@/test/session-user'

const postCreateCheckoutSession = vi.hoisted(() => vi.fn())
const useAuth = vi.hoisted(() => vi.fn())
const routerPush = vi.hoisted(() => vi.fn())
const toastError = vi.hoisted(() => vi.fn())

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: routerPush, refresh: vi.fn(), replace: vi.fn() }),
  usePathname: () => '/assets/1',
}))

vi.mock('@/components/auth/auth-context', () => ({
  useAuth: () => useAuth(),
}))

vi.mock('@/lib/payments/checkout-api', () => ({
  CheckoutRequestError: class CheckoutRequestError extends Error {
    status: number
    constructor(status: number, message: string) {
      super(message)
      this.status = status
      this.name = 'CheckoutRequestError'
    }
  },
  postCreateCheckoutSession: (...args: unknown[]) => postCreateCheckoutSession(...args),
}))

vi.mock('sonner', () => ({ toast: { error: (...args: unknown[]) => toastError(...args) } }))

const assign = vi.fn()

describe('AssetPurchaseCard', () => {
  beforeEach(() => {
    assign.mockReset()
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { assign, href: 'http://localhost/assets/a1', origin: 'http://localhost' },
    })
    useAuth.mockReturnValue({
      user: verifiedSeller({ id: 'buyer-1' }),
      status: 'authenticated',
      isAdmin: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
  })

  it('sends anonymous buyers to login', () => {
    useAuth.mockReturnValue({
      user: null,
      status: 'anonymous',
      isAdmin: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetPurchaseCard
          assetId="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
          authorId="bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
          title="Pack"
          price={9}
          checkoutConfigured
          returnPath="/assets/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        />
      </QueryClientProvider>,
    )
    expect(screen.getByRole('link', { name: /sign in to purchase/i })).toHaveAttribute(
      'href',
      expect.stringContaining('/login'),
    )
  })

  it('disables checkout when payments are unavailable', () => {
    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetPurchaseCard
          assetId="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
          authorId="bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
          title="Pack"
          price={9}
          checkoutConfigured={false}
          returnPath="/assets/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        />
      </QueryClientProvider>,
    )
    expect(screen.getByRole('button', { name: /checkout unavailable/i })).toBeDisabled()
  })

  it('does not treat API failure as success and never hits Stripe', async () => {
    const user = userEvent.setup()
    postCreateCheckoutSession.mockRejectedValueOnce(new Error('gateway'))
    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetPurchaseCard
          assetId="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
          authorId="bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
          title="Pack"
          price={9}
          checkoutConfigured
          returnPath="/assets/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        />
      </QueryClientProvider>,
    )
    await user.click(screen.getByRole('button', { name: /buy now/i }))
    await waitFor(() => expect(toastError).toHaveBeenCalled())
    expect(assign).not.toHaveBeenCalled()
  })

  it('starts checkout with the expected payload when available', async () => {
    const user = userEvent.setup()
    postCreateCheckoutSession.mockResolvedValueOnce({
      checkoutUrl: 'https://checkout.stripe.test/c/pay_123',
      checkoutIntentId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
    })
    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetPurchaseCard
          assetId="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
          authorId="bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
          title="Pack"
          price={9}
          checkoutConfigured
          returnPath="/assets/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        />
      </QueryClientProvider>,
    )
    await user.click(screen.getByRole('button', { name: /buy now/i }))
    await waitFor(() => expect(postCreateCheckoutSession).toHaveBeenCalledTimes(1))
    expect(assign).toHaveBeenCalledWith('https://checkout.stripe.test/c/pay_123')
  })
})
