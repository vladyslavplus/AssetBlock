import { cleanup, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { PostCheckoutReviewBanner } from './post-checkout-review-banner'
import * as checkoutApi from '@/lib/payments/checkout-api'
import { writePendingCheckoutContext } from '@/lib/reviews/review-constants'
import { renderWithQueryClient } from '@/test/render'

vi.mock('@/components/reviews/leave-review-dialog', () => ({
  LeaveReviewDialog: ({ open, assetTitle }: { open: boolean; assetTitle: string }) =>
    open ? <div data-testid="leave-review-dialog">Dialog for {assetTitle}</div> : null,
}))

describe('PostCheckoutReviewBanner', () => {
  beforeEach(() => {
    sessionStorage.clear()
    vi.restoreAllMocks()
  })

  afterEach(() => {
    cleanup()
    sessionStorage.clear()
  })

  it('renders nothing when there is no pending checkout context', async () => {
    const { container } = renderWithQueryClient(<PostCheckoutReviewBanner />)
    await waitFor(() => {
      expect(container.firstChild).toBeNull()
    })
  })

  it('renders processing state when status is pending', async () => {
    writePendingCheckoutContext({
      checkoutIntentId: '11111111-1111-4111-8111-111111111111',
      kind: 'asset',
      assetId: '22222222-2222-4222-8222-222222222222',
    })

    vi.spyOn(checkoutApi, 'fetchCheckoutStatus').mockResolvedValue({
      status: 'pending',
      checkoutIntentId: '11111111-1111-4111-8111-111111111111',
      orderId: null,
      productTitle: 'Pending Asset',
      assetId: '22222222-2222-4222-8222-222222222222',
      bundleId: null,
    })

    renderWithQueryClient(<PostCheckoutReviewBanner />)

    expect(await screen.findByText('Processing payment')).toBeInTheDocument()
    expect(
      screen.getByText('Waiting for payment confirmation. This usually takes a few seconds.'),
    ).toBeInTheDocument()
  })

  it('renders completed asset review banner and invalidates queries on completed status', async () => {
    writePendingCheckoutContext({
      checkoutIntentId: '11111111-1111-4111-8111-111111111111',
      kind: 'asset',
      assetId: '22222222-2222-4222-8222-222222222222',
    })

    vi.spyOn(checkoutApi, 'fetchCheckoutStatus').mockResolvedValue({
      status: 'completed',
      checkoutIntentId: '11111111-1111-4111-8111-111111111111',
      orderId: '33333333-3333-4333-8333-333333333333',
      productTitle: 'Fantasy Sword 3D',
      assetId: '22222222-2222-4222-8222-222222222222',
      bundleId: null,
    })

    renderWithQueryClient(<PostCheckoutReviewBanner />)

    expect(await screen.findByText('How was your purchase?')).toBeInTheDocument()
    expect(screen.getByText(/Fantasy Sword 3D/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Rate now' })).toBeInTheDocument()

    // Test opening review dialog
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Rate now' }))
    expect(screen.getByTestId('leave-review-dialog')).toBeInTheDocument()
  })

  it('renders bundle unlocked banner on completed bundle status', async () => {
    writePendingCheckoutContext({
      checkoutIntentId: '11111111-1111-4111-8111-111111111111',
      kind: 'bundle',
      bundleId: '44444444-4444-4444-8444-444444444444',
    })

    vi.spyOn(checkoutApi, 'fetchCheckoutStatus').mockResolvedValue({
      status: 'completed',
      checkoutIntentId: '11111111-1111-4111-8111-111111111111',
      orderId: '33333333-3333-4333-8333-333333333333',
      productTitle: 'Complete RPG Bundle',
      assetId: null,
      bundleId: '44444444-4444-4444-8444-444444444444',
    })

    renderWithQueryClient(<PostCheckoutReviewBanner />)

    expect(await screen.findByText('Bundle unlocked')).toBeInTheDocument()
    expect(screen.getByText(/Complete RPG Bundle/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open library' })).toBeInTheDocument()
  })

  it('renders payment not completed when status is cancelled', async () => {
    writePendingCheckoutContext({
      checkoutIntentId: '11111111-1111-4111-8111-111111111111',
      kind: 'asset',
      assetId: '22222222-2222-4222-8222-222222222222',
    })

    vi.spyOn(checkoutApi, 'fetchCheckoutStatus').mockResolvedValue({
      status: 'cancelled',
      checkoutIntentId: '11111111-1111-4111-8111-111111111111',
      orderId: null,
      productTitle: 'Cancelled Asset',
      assetId: '22222222-2222-4222-8222-222222222222',
      bundleId: null,
    })

    renderWithQueryClient(<PostCheckoutReviewBanner />)

    expect(await screen.findByText('Payment not completed')).toBeInTheDocument()

    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Dismiss' }))

    await waitFor(() => {
      expect(screen.queryByText('Payment not completed')).not.toBeInTheDocument()
    })
  })

  it('renders payment not completed when API returns malformed or error response', async () => {
    writePendingCheckoutContext({
      checkoutIntentId: '11111111-1111-4111-8111-111111111111',
      kind: 'asset',
      assetId: '22222222-2222-4222-8222-222222222222',
    })

    vi.spyOn(checkoutApi, 'fetchCheckoutStatus').mockRejectedValue(
      new checkoutApi.CheckoutRequestError(500, 'Server error'),
    )

    renderWithQueryClient(<PostCheckoutReviewBanner />)

    expect(
      await screen.findByText('Payment not completed', {}, { timeout: 3000 }),
    ).toBeInTheDocument()
  })
})
