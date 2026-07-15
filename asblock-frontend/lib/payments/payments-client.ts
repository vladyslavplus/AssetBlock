import type { CreateCheckoutRequest } from '@/lib/payments/payments-schemas'

/**
 * Checkout body: only assetId. Redirect URLs come from server Stripe:Default* options.
 */
export function buildCheckoutJsonBody(assetId: string): CreateCheckoutRequest {
  return { assetId }
}
