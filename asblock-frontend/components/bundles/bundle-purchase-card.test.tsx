import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { BundlePurchaseCard } from '@/components/bundles/bundle-purchase-card'
import { renderWithQueryClient } from '@/test/render'
import { verifiedSeller } from '@/test/session-user'

const postCreateBundleCheckoutSession = vi.hoisted(() => vi.fn())
const useAuth = vi.hoisted(() => vi.fn())
const routerPush = vi.hoisted(() => vi.fn())
const toastError = vi.hoisted(() => vi.fn())

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: routerPush, refresh: vi.fn(), replace: vi.fn() }),
  usePathname: () => '/bundles/b1',
}))

vi.mock('@/components/auth/auth-context', () => ({
  useAuth: () => useAuth(),
}))

vi.mock('@/lib/payments/checkout-api', () => ({
  CheckoutRequestError: class CheckoutRequestError extends Error {
    status: number
    code?: string
    constructor(status: number, message: string, code?: string) {
      super(message)
      this.status = status
      this.code = code
      this.name = 'CheckoutRequestError'
    }
  },
  postCreateBundleCheckoutSession: (...args: unknown[]) => postCreateBundleCheckoutSession(...args),
}))

vi.mock('sonner', () => ({ toast: { error: (...args: unknown[]) => toastError(...args) } }))

const assign = vi.fn()

describe('BundlePurchaseCard', () => {
  beforeEach(() => {
    assign.mockReset()
    toastError.mockReset()
    routerPush.mockReset()
    postCreateBundleCheckoutSession.mockReset()
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { assign, href: 'http://localhost/bundles/b1', origin: 'http://localhost' },
    })
    useAuth.mockReturnValue({
      user: verifiedSeller({ id: 'buyer-1' }),
      status: 'authenticated',
      isAdmin: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
  })

  it('shows ownership error toast when checkout fails with ERR_BUNDLE_CONTAINS_OWNED_ASSET', async () => {
    const user = userEvent.setup()
    const { CheckoutRequestError } = await import('@/lib/payments/checkout-api')
    postCreateBundleCheckoutSession.mockRejectedValueOnce(
      new CheckoutRequestError(409, 'Conflict occurred', 'ERR_BUNDLE_CONTAINS_OWNED_ASSET'),
    )

    renderWithQueryClient(
      <BundlePurchaseCard
        bundleId="bundle-1"
        sellerId="seller-1"
        title="Mega Bundle"
        price={49}
        listPriceTotal={100}
        savingsAmount={51}
        savingsPercent={51}
        isAvailable={true}
        items={[]}
        checkoutConfigured={true}
        returnPath="/bundles/bundle-1"
      />,
    )

    const buyButton = screen.getByRole('button', { name: /buy bundle/i })
    await user.click(buyButton)

    await waitFor(() => {
      expect(toastError).toHaveBeenCalledWith(
        'You already own an item in this bundle. Bundle purchase unavailable.',
      )
    })
    expect(assign).not.toHaveBeenCalled()
  })

  it('shows standard error message when checkout fails with 409 ERR_BUNDLE_UNAVAILABLE', async () => {
    const user = userEvent.setup()
    const { CheckoutRequestError } = await import('@/lib/payments/checkout-api')
    postCreateBundleCheckoutSession.mockRejectedValueOnce(
      new CheckoutRequestError(
        409,
        'This bundle is currently unavailable.',
        'ERR_BUNDLE_UNAVAILABLE',
      ),
    )

    renderWithQueryClient(
      <BundlePurchaseCard
        bundleId="bundle-1"
        sellerId="seller-1"
        title="Mega Bundle"
        price={49}
        listPriceTotal={100}
        savingsAmount={51}
        savingsPercent={51}
        isAvailable={true}
        items={[]}
        checkoutConfigured={true}
        returnPath="/bundles/bundle-1"
      />,
    )

    const buyButton = screen.getByRole('button', { name: /buy bundle/i })
    await user.click(buyButton)

    await waitFor(() => {
      expect(toastError).toHaveBeenCalledWith('This bundle is currently unavailable.')
    })
    expect(assign).not.toHaveBeenCalled()
  })
})
