import { describe, expect, it } from 'vitest'
import { checkoutKeys, checkoutStatusQueryOptions } from '@/lib/payments/checkout-query'

describe('checkoutKeys', () => {
  it('builds stable query keys for checkout status', () => {
    expect(checkoutKeys.all).toEqual(['checkout-status'])
    expect(checkoutKeys.status('intent-123')).toEqual(['checkout-status', 'intent-123'])
  })

  it('builds query options that enable query only when intentId is provided', () => {
    const enabledOptions = checkoutStatusQueryOptions('intent-123')
    expect(enabledOptions.enabled).toBe(true)
    expect(enabledOptions.queryKey).toEqual(['checkout-status', 'intent-123'])

    const disabledOptions = checkoutStatusQueryOptions(null)
    expect(disabledOptions.enabled).toBe(false)
  })
})
