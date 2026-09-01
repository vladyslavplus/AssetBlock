import { afterEach, describe, expect, it } from 'vitest'
import { apiUrl, getPublicApiBaseUrl, getServerApiBaseUrl } from './api-config'

describe('api-config', () => {
  const originalPublic = process.env.NEXT_PUBLIC_API_BASE_URL
  const originalServer = process.env.ASSETBLOCK_API_BASE_URL

  afterEach(() => {
    if (originalPublic === undefined) {
      delete process.env.NEXT_PUBLIC_API_BASE_URL
    } else {
      process.env.NEXT_PUBLIC_API_BASE_URL = originalPublic
    }

    if (originalServer === undefined) {
      delete process.env.ASSETBLOCK_API_BASE_URL
    } else {
      process.env.ASSETBLOCK_API_BASE_URL = originalServer
    }
  })

  describe('getPublicApiBaseUrl', () => {
    it('returns trimmed URL without trailing slash', () => {
      process.env.NEXT_PUBLIC_API_BASE_URL = ' https://api.example.com/ '
      expect(getPublicApiBaseUrl()).toBe('https://api.example.com')
    })

    it('throws when NEXT_PUBLIC_API_BASE_URL is not set', () => {
      delete process.env.NEXT_PUBLIC_API_BASE_URL
      expect(() => getPublicApiBaseUrl()).toThrowError(/NEXT_PUBLIC_API_BASE_URL/)
    })
  })

  describe('getServerApiBaseUrl', () => {
    it('returns trimmed URL without trailing slash when ASSETBLOCK_API_BASE_URL is set', () => {
      process.env.ASSETBLOCK_API_BASE_URL = ' http://localhost:5088/// '
      expect(getServerApiBaseUrl()).toBe('http://localhost:5088')
    })

    it('throws when ASSETBLOCK_API_BASE_URL is not set, even if NEXT_PUBLIC_API_BASE_URL is present', () => {
      delete process.env.ASSETBLOCK_API_BASE_URL
      process.env.NEXT_PUBLIC_API_BASE_URL = 'https://api.example.com'
      expect(() => getServerApiBaseUrl()).toThrowError(/ASSETBLOCK_API_BASE_URL/)
    })
  })

  describe('apiUrl', () => {
    it('builds full URL correctly with and without leading slash in path', () => {
      process.env.NEXT_PUBLIC_API_BASE_URL = 'https://api.example.com'
      expect(apiUrl('/api/assets')).toBe('https://api.example.com/api/assets')
      expect(apiUrl('api/assets')).toBe('https://api.example.com/api/assets')
    })
  })
})
