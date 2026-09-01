import { describe, expect, it } from 'vitest'
import { parseCatalogUrlParams, serializeCatalogUrlParams } from '@/lib/catalog/catalog-url-state'
import { DEFAULT_CATALOG_FILTERS } from '@/lib/catalog/catalog-filters'

describe('parseCatalogUrlParams', () => {
  it('returns default filters for empty search params', () => {
    const sp = new URLSearchParams()
    expect(parseCatalogUrlParams(sp)).toEqual(DEFAULT_CATALOG_FILTERS)
  })

  it('parses valid search params correctly', () => {
    const sp = new URLSearchParams({
      search: 'spaceships',
      categoryId: 'e30e1673-98a4-4a25-9ef3-5ce2214fa761',
      tags: 'scifi,modular',
      minPrice: '10',
      maxPrice: '100',
      sortBy: 'Price',
      sortDirection: 'ASC',
      page: '3',
    })

    const parsed = parseCatalogUrlParams(sp)
    expect(parsed.search).toBe('spaceships')
    expect(parsed.categoryId).toBe('e30e1673-98a4-4a25-9ef3-5ce2214fa761')
    expect(parsed.tags).toEqual(['scifi', 'modular'])
    expect(parsed.minPrice).toBe(10)
    expect(parsed.maxPrice).toBe(100)
    expect(parsed.sortBy).toBe('Price')
    expect(parsed.sortDirection).toBe('ASC')
    expect(parsed.page).toBe(3)
  })

  it('sanitizes malformed numbers, non-integers, invalid UUIDs, and invalid sort values safely', () => {
    const sp = new URLSearchParams({
      categoryId: 'not-a-valid-uuid-123',
      minPrice: 'invalid-number',
      maxPrice: '-50',
      sortBy: 'INVALID_SORT',
      sortDirection: 'INVALID_DIR',
      page: '2junk',
    })

    const parsed = parseCatalogUrlParams(sp)
    expect(parsed.categoryId).toBe('')
    expect(parsed.minPrice).toBeNull()
    expect(parsed.maxPrice).toBeNull()
    expect(parsed.sortBy).toBe('CreatedAt')
    expect(parsed.sortDirection).toBe('DESC')
    expect(parsed.page).toBe(1)
  })

  it('rejects decimal, zero, negative, and overflow page numbers', () => {
    expect(parseCatalogUrlParams(new URLSearchParams({ page: '1.5' })).page).toBe(1)
    expect(parseCatalogUrlParams(new URLSearchParams({ page: '0' })).page).toBe(1)
    expect(parseCatalogUrlParams(new URLSearchParams({ page: '-5' })).page).toBe(1)
    expect(parseCatalogUrlParams(new URLSearchParams({ page: '999999999999999999999' })).page).toBe(
      1,
    )
    expect(parseCatalogUrlParams(new URLSearchParams({ page: 'abc' })).page).toBe(1)
  })
})

describe('serializeCatalogUrlParams', () => {
  it('omits defaults and returns empty search params for default state', () => {
    const sp = serializeCatalogUrlParams(DEFAULT_CATALOG_FILTERS)
    expect(sp.toString()).toBe('')
  })

  it('serializes only non-default parameters and valid category UUIDs deterministically', () => {
    const sp = serializeCatalogUrlParams({
      search: 'robot',
      categoryId: 'e30e1673-98a4-4a25-9ef3-5ce2214fa761',
      tags: ['ai', '3d'],
      minPrice: 5,
      maxPrice: 50,
      sortBy: 'Title',
      sortDirection: 'ASC',
      page: 2,
    })

    expect(sp.get('search')).toBe('robot')
    expect(sp.get('categoryId')).toBe('e30e1673-98a4-4a25-9ef3-5ce2214fa761')
    expect(sp.get('tags')).toBe('ai,3d')
    expect(sp.get('minPrice')).toBe('5')
    expect(sp.get('maxPrice')).toBe('50')
    expect(sp.get('sortBy')).toBe('Title')
    expect(sp.get('page')).toBe('2')
  })

  it('ignores invalid category UUID when serializing', () => {
    const sp = serializeCatalogUrlParams({
      categoryId: 'invalid-cat',
    })
    expect(sp.has('categoryId')).toBe(false)
  })
})
