/**
 * Checkout redirect URLs: backend validator requires HTTPS when SuccessUrl/CancelUrl are sent.
 * For http://localhost (typical Next dev), omit URLs and use Stripe:Default* in API config.
 */
export function buildCheckoutJsonBody(assetId: string): Record<string, string> {
  const body: Record<string, string> = { assetId };
  if (typeof window !== "undefined" && window.location.protocol === "https:") {
    const origin = window.location.origin;
    body.successUrl = `${origin}/checkout/success`;
    body.cancelUrl = `${origin}/checkout/cancel`;
  }
  return body;
}
