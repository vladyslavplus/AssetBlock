import type { CheckoutAttributionBrowser } from '@/lib/analytics/telemetry-schemas'
import { isDoNotTrackEnabled } from '@/lib/analytics/telemetry-client'
import type {
  CreateBundleCheckoutRequest,
  CreateCheckoutRequest,
} from '@/lib/payments/payments-schemas'

/**
 * Checkout body: assetId plus optional attribution. Redirect URLs come from server Stripe options.
 */
export function buildCheckoutJsonBody(
  assetId: string,
  attribution?: CheckoutAttributionBrowser,
): CreateCheckoutRequest {
  const body: CreateCheckoutRequest = { assetId }
  if (attribution) {
    body.attribution = attribution
  }
  if (isDoNotTrackEnabled()) {
    body.doNotTrack = true
  }
  return body
}

/** Bundle checkout body: bundleId plus optional attribution. Redirect URLs come from server Stripe options. */
export function buildBundleCheckoutJsonBody(
  bundleId: string,
  attribution?: CheckoutAttributionBrowser,
): CreateBundleCheckoutRequest {
  const body: CreateBundleCheckoutRequest = { bundleId }
  if (attribution) {
    body.attribution = attribution
  }
  if (isDoNotTrackEnabled()) {
    body.doNotTrack = true
  }
  return body
}
