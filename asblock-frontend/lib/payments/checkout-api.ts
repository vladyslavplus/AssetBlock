import type { CheckoutAttributionBrowser } from '@/lib/analytics/telemetry-schemas'
import { getApiErrorMessage, parseApiErrorBody, readApiResponseBody } from '@/lib/http/api-errors'
import { buildBundleCheckoutJsonBody, buildCheckoutJsonBody } from '@/lib/payments/payments-client'
import {
  checkoutStatusResponseSchema,
  createCheckoutResponseSchema,
} from '@/lib/payments/payments-schemas'
import type { CheckoutStatusResponse, CreateCheckoutResponse } from '@/lib/payments/payments-types'

export class CheckoutRequestError extends Error {
  readonly status: number
  readonly code?: string
  readonly traceId?: string

  constructor(status: number, message: string, body?: unknown) {
    super(message)
    this.name = 'CheckoutRequestError'
    this.status = status
    const parsed = parseApiErrorBody(body)
    this.code = parsed?.code
    this.traceId = parsed?.traceId
  }
}

async function postCheckout(path: string, body: unknown): Promise<CreateCheckoutResponse> {
  const res = await fetch(path, {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const raw = await readApiResponseBody(res)

  if (!res.ok) {
    const fallback = typeof raw === 'string' ? raw : `Checkout failed (${res.status})`
    throw new CheckoutRequestError(res.status, getApiErrorMessage(raw, fallback), raw)
  }

  const parsed = createCheckoutResponseSchema.safeParse(raw)
  if (!parsed.success) {
    throw new CheckoutRequestError(
      res.status,
      'Checkout did not return a payment URL and checkout intent id.',
    )
  }
  return parsed.data
}

export async function postCreateCheckoutSession(
  assetId: string,
  attribution?: CheckoutAttributionBrowser,
): Promise<CreateCheckoutResponse> {
  return postCheckout('/api/payments/checkout', buildCheckoutJsonBody(assetId, attribution))
}

export async function postCreateBundleCheckoutSession(
  bundleId: string,
  attribution?: CheckoutAttributionBrowser,
): Promise<CreateCheckoutResponse> {
  return postCheckout(
    '/api/payments/checkout/bundles',
    buildBundleCheckoutJsonBody(bundleId, attribution),
  )
}

export async function fetchCheckoutStatus(
  checkoutIntentId: string,
): Promise<CheckoutStatusResponse> {
  const res = await fetch(`/api/payments/checkout/${encodeURIComponent(checkoutIntentId)}/status`, {
    credentials: 'include',
    cache: 'no-store',
  })
  const raw = await readApiResponseBody(res)
  if (!res.ok) {
    const fallback = typeof raw === 'string' ? raw : `Checkout status failed (${res.status})`
    throw new CheckoutRequestError(res.status, getApiErrorMessage(raw, fallback), raw)
  }
  const parsed = checkoutStatusResponseSchema.safeParse(raw)
  if (!parsed.success) {
    throw new CheckoutRequestError(res.status, 'Checkout returned an invalid status response.')
  }
  return parsed.data
}
