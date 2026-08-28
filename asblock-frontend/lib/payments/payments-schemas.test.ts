import { describe, expect, it } from 'vitest'
import { CHECKOUT_FULFILLMENT_STATUSES, checkoutStatusResponseSchema } from './payments-schemas'
import type { CheckoutFulfillmentStatus } from './payments-types'

describe('payments-schemas', () => {
  it('defines stable CHECKOUT_FULFILLMENT_STATUSES array', () => {
    expect(CHECKOUT_FULFILLMENT_STATUSES).toEqual(['pending', 'completed', 'cancelled'])
  })

  describe('checkoutStatusResponseSchema parameterized over known statuses', () => {
    const validCases: Array<{
      status: CheckoutFulfillmentStatus
      orderId: string | null
      assetId: string | null
      bundleId: string | null
    }> = [
      {
        status: 'pending',
        orderId: null,
        assetId: '33333333-3333-4333-8333-333333333333',
        bundleId: null,
      },
      {
        status: 'completed',
        orderId: '22222222-2222-4222-8222-222222222222',
        assetId: '33333333-3333-4333-8333-333333333333',
        bundleId: null,
      },
      {
        status: 'completed',
        orderId: '22222222-2222-4222-8222-222222222222',
        assetId: null,
        bundleId: '44444444-4444-4444-8444-444444444444',
      },
      {
        status: 'cancelled',
        orderId: null,
        assetId: '33333333-3333-4333-8333-333333333333',
        bundleId: null,
      },
    ]

    it.each(validCases)(
      'validates $status status successfully (orderId: $orderId, assetId: $assetId, bundleId: $bundleId)',
      ({ status, orderId, assetId, bundleId }) => {
        const payload = {
          status,
          checkoutIntentId: '11111111-1111-4111-8111-111111111111',
          orderId,
          productTitle: 'Test Product Title',
          assetId,
          bundleId,
        }

        const parsed = checkoutStatusResponseSchema.safeParse(payload)
        expect(parsed.success).toBe(true)
        if (parsed.success) {
          expect(parsed.data.status).toBe(status)
          expect(parsed.data.orderId).toBe(orderId)
          expect(parsed.data.productTitle).toBe('Test Product Title')
        }
      },
    )
  })

  describe('rejection of malformed / unknown shapes', () => {
    it('rejects unknown status values', () => {
      const invalid = {
        status: 'refunded',
        checkoutIntentId: '11111111-1111-4111-8111-111111111111',
        orderId: null,
        productTitle: 'Test Asset',
        assetId: null,
        bundleId: null,
      }
      expect(checkoutStatusResponseSchema.safeParse(invalid).success).toBe(false)
    })

    it('rejects non-uuid checkoutIntentId', () => {
      const invalid = {
        status: 'completed',
        checkoutIntentId: 'not-a-uuid',
        orderId: '22222222-2222-4222-8222-222222222222',
        productTitle: 'Test Asset',
        assetId: null,
        bundleId: null,
      }
      expect(checkoutStatusResponseSchema.safeParse(invalid).success).toBe(false)
    })

    it('rejects non-uuid orderId when non-null', () => {
      const invalid = {
        status: 'completed',
        checkoutIntentId: '11111111-1111-4111-8111-111111111111',
        orderId: 'invalid-order-uuid',
        productTitle: 'Test Asset',
        assetId: null,
        bundleId: null,
      }
      expect(checkoutStatusResponseSchema.safeParse(invalid).success).toBe(false)
    })

    it('rejects missing required fields', () => {
      expect(checkoutStatusResponseSchema.safeParse({}).success).toBe(false)
      expect(
        checkoutStatusResponseSchema.safeParse({
          status: 'pending',
          checkoutIntentId: '11111111-1111-4111-8111-111111111111',
        }).success,
      ).toBe(false)
    })
  })
})
