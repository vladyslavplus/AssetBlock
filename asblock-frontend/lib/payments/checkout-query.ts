import { queryOptions } from '@tanstack/react-query'
import { fetchCheckoutStatus } from '@/lib/payments/checkout-api'

export const checkoutKeys = {
  all: ['checkout-status'] as const,
  status: (checkoutIntentId: string) => [...checkoutKeys.all, checkoutIntentId] as const,
}

export function checkoutStatusQueryOptions(checkoutIntentId: string | null | undefined) {
  return queryOptions({
    queryKey: checkoutKeys.status(checkoutIntentId ?? ''),
    queryFn: () => {
      if (!checkoutIntentId) {
        throw new Error('Missing checkout intent id')
      }
      return fetchCheckoutStatus(checkoutIntentId)
    },
    enabled: Boolean(checkoutIntentId),
  })
}
