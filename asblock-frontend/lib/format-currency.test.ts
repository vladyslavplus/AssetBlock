import { describe, expect, it } from 'vitest'
import { formatCompactMoneyCents, formatMoneyCents, formatUsdWhole } from '@/lib/format-currency'

describe('formatUsdWhole', () => {
  it('formats numeric amounts as whole USD currency', () => {
    expect(formatUsdWhole(0)).toBe('$0')
    expect(formatUsdWhole(49)).toBe('$49')
    expect(formatUsdWhole(1250)).toBe('$1,250')
    expect(formatUsdWhole(-25)).toBe('-$25')
  })
})

describe('formatMoneyCents', () => {
  it('formats USD cents with exact decimal places', () => {
    expect(formatMoneyCents(0)).toBe('$0.00')
    expect(formatMoneyCents(1250)).toBe('$12.50')
    expect(formatMoneyCents(4999)).toBe('$49.99')
    expect(formatMoneyCents(-500)).toBe('-$5.00')
  })

  it('supports alternative currency codes', () => {
    expect(formatMoneyCents(1500, 'EUR')).toContain('15.00')
  })
})

describe('formatCompactMoneyCents', () => {
  it('formats values under 1000 without k suffix', () => {
    expect(formatCompactMoneyCents(0)).toBe('$0.0')
    expect(formatCompactMoneyCents(5000)).toBe('$50.0')
    expect(formatCompactMoneyCents(99900)).toBe('$999')
  })

  it('formats values from 1000 to 9999 with 1 decimal k suffix', () => {
    expect(formatCompactMoneyCents(120000)).toBe('$1.2k')
    expect(formatCompactMoneyCents(950000)).toBe('$9.5k')
  })

  it('formats values from 10000+ with 0 decimals k suffix', () => {
    expect(formatCompactMoneyCents(1000000)).toBe('$10k')
    expect(formatCompactMoneyCents(5500000)).toBe('$55k')
  })

  it('formats negative values accurately', () => {
    expect(formatCompactMoneyCents(-150000)).toBe('$-1.5k')
  })
})
