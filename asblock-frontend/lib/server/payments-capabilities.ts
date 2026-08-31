import { fetchBackendPublic } from '@/lib/server/fetch-backend'
import type { PaymentsCapabilities } from '@/lib/payments/payments-types'

/**
 * Public backend probe: whether Stripe checkout is configured (no Stripe API call).
 */
export async function fetchPaymentsCapabilitiesServer(): Promise<PaymentsCapabilities> {
  try {
    const res = await fetchBackendPublic('/api/payments/capabilities')
    if (!res.ok) {
      return { checkoutConfigured: false }
    }
    const data = (await res.json()) as Partial<PaymentsCapabilities>
    return { checkoutConfigured: Boolean(data.checkoutConfigured) }
  } catch {
    return { checkoutConfigured: false }
  }
}
