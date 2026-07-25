import type {
  CreateBundleCheckoutRequest,
  CreateCheckoutRequest,
} from '@/lib/payments/payments-schemas'

/**
 * Checkout body: only assetId. Redirect URLs come from server Stripe:Default* options.
 */
export function buildCheckoutJsonBody(assetId: string): CreateCheckoutRequest {
  return { assetId }
}

/** Bundle checkout body: only bundleId. Redirect URLs come from server Stripe options. */
export function buildBundleCheckoutJsonBody(bundleId: string): CreateBundleCheckoutRequest {
  return { bundleId }
}
