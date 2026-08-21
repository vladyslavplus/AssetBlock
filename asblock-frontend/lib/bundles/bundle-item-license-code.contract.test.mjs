import assert from 'node:assert/strict'
import { test } from 'node:test'
import { z } from 'zod'

const licenseCodeSchema = z.string().nullable()

test('accepts PERSONAL and COMMERCIAL strings', () => {
  assert.equal(licenseCodeSchema.parse('PERSONAL'), 'PERSONAL')
  assert.equal(licenseCodeSchema.parse('COMMERCIAL'), 'COMMERCIAL')
  assert.equal(licenseCodeSchema.parse(null), null)
})

test('rejects enum ordinal numbers', () => {
  assert.throws(() => licenseCodeSchema.parse(0), /Expected string/)
  assert.throws(() => licenseCodeSchema.parse(1), /Expected string/)
})
