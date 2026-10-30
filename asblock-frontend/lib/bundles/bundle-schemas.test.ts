import { describe, expect, it } from 'vitest'
import { BUNDLE_MIN_ITEMS, bundleFormSchema } from './bundle-schemas'

const VALID_ID = '123e4567-e89b-12d3-a456-426614174000'

describe('bundleFormSchema assetIds', () => {
  it('shows the uuid message for a malformed id', () => {
    const result = bundleFormSchema.safeParse({
      title: 'Bundle',
      assetIds: ['not-a-uuid'],
    })

    expect(result.success).toBe(false)
    expect(
      result.error?.issues.find((issue) => issue.path.join('.') === 'assetIds.0')?.message,
    ).toBe('Invalid uuid')
  })

  it('shows the minimum count message for a too-short array', () => {
    const result = bundleFormSchema.safeParse({
      title: 'Bundle',
      assetIds: Array.from({ length: BUNDLE_MIN_ITEMS - 1 }, () => VALID_ID),
    })

    expect(result.success).toBe(false)
    expect(result.error?.issues.find((issue) => issue.path.join('.') === 'assetIds')?.message).toBe(
      `Select at least ${BUNDLE_MIN_ITEMS} assets`,
    )
  })
})
