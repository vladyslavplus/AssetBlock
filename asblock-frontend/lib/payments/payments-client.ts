/**
 * Checkout body: only assetId. Redirect URLs come from server Stripe:Default* options.
 */
export function buildCheckoutJsonBody(assetId: string): { assetId: string } {
  return { assetId };
}
