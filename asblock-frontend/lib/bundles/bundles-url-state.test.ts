import { describe, expect, it } from 'vitest'
import { parseBundlesUrlParams, serializeBundlesUrlParams } from '@/lib/bundles/bundles-url-state'

describe('bundles-url-state', () => {
  it('parses empty and valid search params', () => {
    expect(parseBundlesUrlParams(new URLSearchParams())).toEqual({ search: '', page: 1 })
    expect(parseBundlesUrlParams(new URLSearchParams({ search: 'starter', page: '2' }))).toEqual({
      search: 'starter',
      page: 2,
    })
  })

  it('sanitizes invalid and malformed page numbers', () => {
    expect(parseBundlesUrlParams(new URLSearchParams({ page: 'invalid' }))).toEqual({
      search: '',
      page: 1,
    })
    expect(parseBundlesUrlParams(new URLSearchParams({ page: '2junk' }))).toEqual({
      search: '',
      page: 1,
    })
    expect(parseBundlesUrlParams(new URLSearchParams({ page: '1.5' }))).toEqual({
      search: '',
      page: 1,
    })
    expect(parseBundlesUrlParams(new URLSearchParams({ page: '0' }))).toEqual({
      search: '',
      page: 1,
    })
    expect(parseBundlesUrlParams(new URLSearchParams({ page: '-10' }))).toEqual({
      search: '',
      page: 1,
    })
    expect(parseBundlesUrlParams(new URLSearchParams({ page: '99999999999999999999' }))).toEqual({
      search: '',
      page: 1,
    })
  })

  it('serializes non-default values deterministically', () => {
    expect(serializeBundlesUrlParams({ search: '', page: 1 }).toString()).toBe('')
    expect(serializeBundlesUrlParams({ search: 'starter', page: 2 }).toString()).toBe(
      'search=starter&page=2',
    )
  })
})
