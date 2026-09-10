import { describe, expect, it } from 'vitest'
import { buildAssetsQueryParams } from './assets-api'
import { DEFAULT_CATALOG_FILTERS, type CatalogFilters } from './catalog-filters'
import { parseCatalogUrlParams, serializeCatalogUrlParams } from './catalog-url-state'

describe('Catalog sort and search regression contracts', () => {
  it('omits sort params for a relevance search (no explicit sort)', () => {
    const filters: CatalogFilters = {
      ...DEFAULT_CATALOG_FILTERS,
      search: 'lowpoly sword',
      sortBy: 'Relevance',
      page: 1,
    }

    const qs = buildAssetsQueryParams(filters)
    const params = new URLSearchParams(qs)

    // Relevance mode is UI-only: backend decides ranking, so no explicit sort is sent.
    expect(params.get('search')).toBe('lowpoly sword')
    expect(params.get('sortBy')).toBeNull()
    expect(params.get('sortDirection')).toBeNull()
    expect(params.get('page')).toBe('1')
    expect(params.get('pageSize')).toBe('12')
  })

  it('omits sort params when search is active without an explicit sort from URL', () => {
    const parsed = parseCatalogUrlParams(new URLSearchParams('search=lowpoly+sword'))
    expect(parsed.search).toBe('lowpoly sword')
    expect(parsed.sortBy).toBe('Relevance')
    expect(parsed.sortDirection).toBe('DESC')

    // Serializing the parsed relevance state keeps the URL clean (no sort params).
    const serialized = serializeCatalogUrlParams(parsed)
    expect(serialized.get('sortBy')).toBeNull()
    expect(serialized.get('sortDirection')).toBeNull()
  })

  it('sends explicit sort behavior when a custom sortBy is selected', () => {
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

  it('preserves an explicitly chosen CreatedAt sort in the URL during a search', () => {
    const filters: CatalogFilters = {
      ...DEFAULT_CATALOG_FILTERS,
      search: 'castle',
      sortBy: 'CreatedAt',
      sortDirection: 'DESC',
      page: 1,
    }

    const serialized = serializeCatalogUrlParams(filters)
    // Explicit CreatedAt during search is shareable, even though it equals the browse default.
    expect(serialized.get('sortBy')).toBe('CreatedAt')
    expect(serialized.get('sortDirection')).toBeNull() // DESC is the default for CreatedAt

    const parsed = parseCatalogUrlParams(serialized)
    expect(parsed.sortBy).toBe('CreatedAt')
    expect(parsed.sortDirection).toBe('DESC')
    expect(parsed.search).toBe('castle')
  })

  it('round-trips explicit URL sort and preserves shareability', () => {
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

  it('uses relevance for a search URL that carries only search, and browse default without search', () => {
    // Search with no sort params -> relevance mode.
    const searchOnly = new URLSearchParams('search=tree')
    const parsedSearch = parseCatalogUrlParams(searchOnly)
    expect(parsedSearch.search).toBe('tree')
    expect(parsedSearch.sortBy).toBe('Relevance')
    expect(parsedSearch.sortDirection).toBe('DESC')

    // No search, no sort params -> browse default (CreatedAt DESC).
    const emptyParams = new URLSearchParams('')
    const parsedDefault = parseCatalogUrlParams(emptyParams)
    expect(parsedDefault.sortBy).toBe('CreatedAt')
    expect(parsedDefault.sortDirection).toBe('DESC')

    // No search but an explicit non-default sort remains shareable.
    const titleOnly = parseCatalogUrlParams(new URLSearchParams('sortBy=Title'))
    expect(titleOnly.sortBy).toBe('Title')
    expect(titleOnly.sortDirection).toBe('ASC')
  })

  it('clearing search falls back to browse default and never leaves relevance behind', () => {
    const relevance = parseCatalogUrlParams(new URLSearchParams('search=sword'))
    expect(relevance.sortBy).toBe('Relevance')

    // Simulate the UI resetting search with a relevance-mode filter object.
    const cleared = serializeCatalogUrlParams({ ...DEFAULT_CATALOG_FILTERS, sortBy: 'Relevance' })
    expect(cleared.get('sortBy')).toBeNull()
    expect(cleared.toString()).toBe('')

    const parsedCleared = parseCatalogUrlParams(cleared)
    expect(parsedCleared.sortBy).toBe('CreatedAt')
    expect(parsedCleared.sortDirection).toBe('DESC')
  })
})
