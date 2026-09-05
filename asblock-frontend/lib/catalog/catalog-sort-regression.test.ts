import { describe, expect, it } from 'vitest'
import { buildAssetsQueryParams } from './assets-api'
import { DEFAULT_CATALOG_FILTERS, type CatalogFilters } from './catalog-filters'
import { parseCatalogUrlParams, serializeCatalogUrlParams } from './catalog-url-state'

describe('Catalog sort and search regression contracts', () => {
  it('captures current API query parameter construction when search is present', () => {
    const filters: CatalogFilters = {
      ...DEFAULT_CATALOG_FILTERS,
      search: 'lowpoly sword',
      page: 1,
    }

    const qs = buildAssetsQueryParams(filters)
    const params = new URLSearchParams(qs)

    // Current contract: buildAssetsQueryParams always serializes sortBy and sortDirection,
    // even when search is active.
    expect(params.get('search')).toBe('lowpoly sword')
    expect(params.get('sortBy')).toBe('CreatedAt')
    expect(params.get('sortDirection')).toBe('DESC')
    expect(params.get('page')).toBe('1')
    expect(params.get('pageSize')).toBe('12')
  })

  it('captures current explicit sort behavior when custom sortBy is selected', () => {
    const filters: CatalogFilters = {
      ...DEFAULT_CATALOG_FILTERS,
      search: 'plasma rifle',
      sortBy: 'Price',
      sortDirection: 'ASC',
      page: 2,
    }

    const qs = buildAssetsQueryParams(filters)
    const params = new URLSearchParams(qs)

    expect(params.get('search')).toBe('plasma rifle')
    expect(params.get('sortBy')).toBe('Price')
    expect(params.get('sortDirection')).toBe('ASC')
    expect(params.get('page')).toBe('2')
  })

  it('captures URL parameter serialization and parsing for search and sort', () => {
    const initialFilters: Partial<CatalogFilters> = {
      search: 'medieval castle',
      sortBy: 'Title',
      sortDirection: 'ASC',
      page: 3,
    }

    const serialized = serializeCatalogUrlParams(initialFilters)
    expect(serialized.get('search')).toBe('medieval castle')
    expect(serialized.get('sortBy')).toBe('Title')
    // Title defaults to ASC, so serializeCatalogUrlParams omits sortDirection to produce clean URLs
    expect(serialized.get('sortDirection')).toBeNull()
    expect(serialized.get('page')).toBe('3')

    // parseCatalogUrlParams restores default ASC for Title
    const parsed = parseCatalogUrlParams(serialized)
    expect(parsed.search).toBe('medieval castle')
    expect(parsed.sortBy).toBe('Title')
    expect(parsed.sortDirection).toBe('ASC')
    expect(parsed.page).toBe(3)

    // Non-default direction DESC is serialized explicitly
    const descSerialized = serializeCatalogUrlParams({ ...initialFilters, sortDirection: 'DESC' })
    expect(descSerialized.get('sortDirection')).toBe('DESC')
    const descParsed = parseCatalogUrlParams(descSerialized)
    expect(descParsed.sortDirection).toBe('DESC')
  })

  it('preserves default sorting (CreatedAt DESC) when URL has no sort parameters', () => {
    const emptyParams = new URLSearchParams('search=tree')
    const parsed = parseCatalogUrlParams(emptyParams)

    expect(parsed.search).toBe('tree')
    expect(parsed.sortBy).toBe('CreatedAt')
    expect(parsed.sortDirection).toBe('DESC')
  })
})
