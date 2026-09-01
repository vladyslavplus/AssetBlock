import { describe, expect, it } from 'vitest'
import {
  parseCollectionsUrlParams,
  serializeCollectionsUrlParams,
} from '@/lib/collections/collections-url-state'

describe('collections-url-state', () => {
  it('parses empty and valid search params', () => {
    expect(parseCollectionsUrlParams(new URLSearchParams())).toEqual({ search: '', page: 1 })
    expect(
      parseCollectionsUrlParams(new URLSearchParams({ search: 'curated', page: '4' })),
    ).toEqual({ search: 'curated', page: 4 })
  })

  it('sanitizes invalid and malformed page numbers', () => {
    expect(parseCollectionsUrlParams(new URLSearchParams({ page: '-3' }))).toEqual({
      search: '',
      page: 1,
    })
    expect(parseCollectionsUrlParams(new URLSearchParams({ page: 'abc' }))).toEqual({
      search: '',
      page: 1,
    })
    expect(parseCollectionsUrlParams(new URLSearchParams({ page: '2junk' }))).toEqual({
      search: '',
      page: 1,
    })
    expect(parseCollectionsUrlParams(new URLSearchParams({ page: '1.5' }))).toEqual({
      search: '',
      page: 1,
    })
    expect(parseCollectionsUrlParams(new URLSearchParams({ page: '0' }))).toEqual({
      search: '',
      page: 1,
    })
    expect(
      parseCollectionsUrlParams(new URLSearchParams({ page: '99999999999999999999' })),
    ).toEqual({
      search: '',
      page: 1,
    })
  })

  it('serializes non-default values deterministically', () => {
    expect(serializeCollectionsUrlParams({ search: '', page: 1 }).toString()).toBe('')
    expect(serializeCollectionsUrlParams({ search: 'scifi', page: 3 }).toString()).toBe(
      'search=scifi&page=3',
    )
  })
})
