import { describe, expect, it } from 'vitest'
import { marketplacePriceSchema } from './price-schema'

describe('marketplacePriceSchema', () => {
  it('accepts positive prices with at most two decimal places, including numeric strings', () => {
    expect(marketplacePriceSchema.safeParse(19.99).success).toBe(true)
    expect(marketplacePriceSchema.safeParse('0.01').success).toBe(true)
    expect(marketplacePriceSchema.safeParse('999999.99').success).toBe(true)
  })

  it('rejects zero, too-large, over-precise, and non-finite prices', () => {
    expect(marketplacePriceSchema.safeParse(0).success).toBe(false)
    expect(marketplacePriceSchema.safeParse('1000000').success).toBe(false)
    expect(marketplacePriceSchema.safeParse(1.005).success).toBe(false)
    expect(marketplacePriceSchema.safeParse(Number.POSITIVE_INFINITY).success).toBe(false)
    expect(marketplacePriceSchema.safeParse('abc').success).toBe(false)
  })

  it('keeps the form messages for zero and two-decimal violations', () => {
    const zero = marketplacePriceSchema.safeParse(0)
    const precision = marketplacePriceSchema.safeParse(1.005)

    expect(zero.error?.issues[0]?.message).toBe('Price must be greater than zero')
    expect(precision.error?.issues[0]?.message).toBe('Price must have at most two decimal places')
  })
})
