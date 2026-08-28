import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  CheckoutRequestError,
  fetchCheckoutStatus,
  postCreateBundleCheckoutSession,
  postCreateCheckoutSession,
} from './checkout-api'

describe('checkout-api', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  describe('fetchCheckoutStatus', () => {
    it('returns parsed CheckoutStatusResponse when status is pending', async () => {
      const payload = {
        status: 'pending',
        checkoutIntentId: '11111111-1111-4111-8111-111111111111',
        orderId: null,
        productTitle: 'Pending Asset',
        assetId: '22222222-2222-4222-8222-222222222222',
        bundleId: null,
      }

      vi.stubGlobal(
        'fetch',
        vi.fn(async () => {
          return new Response(JSON.stringify(payload), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        }),
      )

      const result = await fetchCheckoutStatus('11111111-1111-4111-8111-111111111111')
      expect(result).toEqual(payload)
    })

    it('returns parsed CheckoutStatusResponse when status is completed', async () => {
      const payload = {
        status: 'completed',
        checkoutIntentId: '11111111-1111-4111-8111-111111111111',
        orderId: '33333333-3333-4333-8333-333333333333',
        productTitle: 'Completed Asset',
        assetId: '22222222-2222-4222-8222-222222222222',
        bundleId: null,
      }

      vi.stubGlobal(
        'fetch',
        vi.fn(async () => {
          return new Response(JSON.stringify(payload), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        }),
      )

      const result = await fetchCheckoutStatus('11111111-1111-4111-8111-111111111111')
      expect(result).toEqual(payload)
    })

    it('returns parsed CheckoutStatusResponse when status is cancelled', async () => {
      const payload = {
        status: 'cancelled',
        checkoutIntentId: '11111111-1111-4111-8111-111111111111',
        orderId: null,
        productTitle: 'Cancelled Asset',
        assetId: '22222222-2222-4222-8222-222222222222',
        bundleId: null,
      }

      vi.stubGlobal(
        'fetch',
        vi.fn(async () => {
          return new Response(JSON.stringify(payload), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        }),
      )

      const result = await fetchCheckoutStatus('11111111-1111-4111-8111-111111111111')
      expect(result).toEqual(payload)
    })

    it('throws CheckoutRequestError when API returns 404 ProblemDetails', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => {
          return new Response(
            JSON.stringify({
              type: 'urn:assetblock:error:ERR_NOT_FOUND',
              title: 'Not Found',
              status: 404,
              detail: 'Checkout intent not found',
            }),
            {
              status: 404,
              headers: { 'Content-Type': 'application/problem+json' },
            },
          )
        }),
      )

      await expect(fetchCheckoutStatus('11111111-1111-4111-8111-111111111111')).rejects.toThrow(
        CheckoutRequestError,
      )
    })

    it('throws CheckoutRequestError when API returns malformed or unknown status payload', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => {
          return new Response(
            JSON.stringify({
              status: 'unknown_status',
              checkoutIntentId: '11111111-1111-4111-8111-111111111111',
            }),
            {
              status: 200,
              headers: { 'Content-Type': 'application/json' },
            },
          )
        }),
      )

      await expect(fetchCheckoutStatus('11111111-1111-4111-8111-111111111111')).rejects.toThrow(
        'Checkout returned an invalid status response.',
      )
    })
  })

  describe('postCreateCheckoutSession and postCreateBundleCheckoutSession', () => {
    it('creates single asset checkout session successfully', async () => {
      const payload = {
        checkoutUrl: 'https://checkout.stripe.com/session_123',
        checkoutIntentId: '11111111-1111-4111-8111-111111111111',
      }

      vi.stubGlobal(
        'fetch',
        vi.fn(async () => {
          return new Response(JSON.stringify(payload), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        }),
      )

      const result = await postCreateCheckoutSession('22222222-2222-4222-8222-222222222222')
      expect(result).toEqual(payload)
    })

    it('creates bundle checkout session successfully', async () => {
      const payload = {
        checkoutUrl: 'https://checkout.stripe.com/session_bundle',
        checkoutIntentId: '11111111-1111-4111-8111-111111111111',
      }

      vi.stubGlobal(
        'fetch',
        vi.fn(async () => {
          return new Response(JSON.stringify(payload), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        }),
      )

      const result = await postCreateBundleCheckoutSession('33333333-3333-4333-8333-333333333333')
      expect(result).toEqual(payload)
    })
  })
})
