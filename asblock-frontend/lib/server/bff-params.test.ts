import { describe, expect, it, vi } from 'vitest'
import { parseOptionalUuidParam, parseUuidParam } from './bff-params'

vi.mock('server-only', () => ({}))

describe('bff-params', () => {
  describe('parseUuidParam', () => {
    it('returns parsed UUID on valid input', () => {
      const validUuid = '123e4567-e89b-12d3-a456-426614174000'
      const result = parseUuidParam('id', validUuid)
      expect(result.ok).toBe(true)
      if (result.ok) {
        expect(result.value).toBe(validUuid)
      }
    })

    it('returns 400 ProblemDetails on non-UUID string', async () => {
      const result = parseUuidParam('id', 'not-a-uuid')
      expect(result.ok).toBe(false)
      if (!result.ok) {
        expect(result.response.status).toBe(400)
        const body = await result.response.json()
        expect(body.code).toBe('ERR_VALIDATION_FAILED')
        expect(body.errors?.id).toBeDefined()
      }
    })

    it('returns 400 ProblemDetails on null or empty string', async () => {
      const resultNull = parseUuidParam('assetId', null)
      expect(resultNull.ok).toBe(false)
      if (!resultNull.ok) {
        expect(resultNull.response.status).toBe(400)
      }

      const resultEmpty = parseUuidParam('assetId', '   ')
      expect(resultEmpty.ok).toBe(false)
      if (!resultEmpty.ok) {
        expect(resultEmpty.response.status).toBe(400)
      }
    })
  })

  describe('parseOptionalUuidParam', () => {
    it('returns null on null or empty string', () => {
      const resultNull = parseOptionalUuidParam('versionId', null)
      expect(resultNull.ok).toBe(true)
      if (resultNull.ok) {
        expect(resultNull.value).toBeNull()
      }

      const resultEmpty = parseOptionalUuidParam('versionId', '')
      expect(resultEmpty.ok).toBe(true)
      if (resultEmpty.ok) {
        expect(resultEmpty.value).toBeNull()
      }
    })

    it('returns parsed UUID on valid input', () => {
      const validUuid = 'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11'
      const result = parseOptionalUuidParam('versionId', validUuid)
      expect(result.ok).toBe(true)
      if (result.ok) {
        expect(result.value).toBe(validUuid)
      }
    })

    it('returns 400 ProblemDetails on invalid UUID when provided', async () => {
      const result = parseOptionalUuidParam('versionId', 'bad-id')
      expect(result.ok).toBe(false)
      if (!result.ok) {
        expect(result.response.status).toBe(400)
        const body = await result.response.json()
        expect(body.code).toBe('ERR_VALIDATION_FAILED')
      }
    })
  })
})
