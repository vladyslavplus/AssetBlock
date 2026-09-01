import { describe, expect, it } from 'vitest'
import { formatDateTimeLocal, formatRelativeTime } from '@/lib/format-date'

describe('formatDateTimeLocal', () => {
  it('formats valid ISO date strings in en-US local format', () => {
    const formatted = formatDateTimeLocal('2026-01-15T10:30:00.000Z')
    expect(formatted).toMatch(/Jan 15, 2026/)
  })

  it('formats Date objects and epoch timestamps', () => {
    const d = new Date(2026, 0, 15, 10, 30, 0)
    expect(formatDateTimeLocal(d)).toMatch(/Jan 15, 2026/)
    expect(formatDateTimeLocal(d.getTime())).toMatch(/Jan 15, 2026/)
  })

  it('safely falls back to input string for invalid dates without throwing', () => {
    expect(formatDateTimeLocal('invalid-date')).toBe('invalid-date')
    expect(formatDateTimeLocal('')).toBe('')
  })
})

describe('formatRelativeTime', () => {
  it('formats relative past and future timestamps', () => {
    const past = new Date(Date.now() - 5 * 60 * 1000).toISOString()
    expect(formatRelativeTime(past)).toMatch(/ago/)

    const future = new Date(Date.now() + 2 * 60 * 60 * 1000).toISOString()
    expect(formatRelativeTime(future)).toMatch(/in /)
  })

  it('safely falls back to input string for invalid dates without throwing', () => {
    expect(formatRelativeTime('not-a-date')).toBe('not-a-date')
    expect(formatRelativeTime('')).toBe('')
  })
})
