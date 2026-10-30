import { describe, expect, it } from 'vitest'
import { isoDateTimeSchema } from './library-schemas'

describe('isoDateTimeSchema', () => {
  it('accepts backend timestamps with 7-digit fractions and offset or Z', () => {
    expect(isoDateTimeSchema.safeParse('2026-10-30T12:34:56.1234567+00:00').success).toBe(true)
    expect(isoDateTimeSchema.safeParse('2026-10-30T12:34:56.1234567Z').success).toBe(true)
  })
})
