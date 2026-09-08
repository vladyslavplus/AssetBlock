import { afterEach, describe, expect, it, vi } from 'vitest'
import { fetchAssetsPage } from './assets-api'
import { DEFAULT_CATALOG_FILTERS, type CatalogFilters } from './catalog-filters'

describe('fetchAssetsPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  const assetRow = {
    id: '11111111-1111-4111-8111-111111111111',
    title: 'Relevant Sword',
    description: 'A sharp blade',
    price: 15,
    categoryId: '22222222-2222-4222-8222-222222222222',
    categoryName: 'Weapons',
    authorId: '33333333-3333-4333-8333-333333333333',
    authorUsername: 'blacksmith',
    createdAt: '2026-01-01T00:00:00Z',
    tags: ['weapon'],
    averageRating: 4.8,
  }

  it('omits sort params and surfaces isTruncated for a relevance search', async () => {
    const fetchMock = vi.fn(
      async (_input: string | URL | Request) =>
        new Response(
          JSON.stringify({
            items: [assetRow],
            totalCount: 3,
            page: 1,
            pageSize: 12,
            isTruncated: true,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const filters: CatalogFilters = {
      ...DEFAULT_CATALOG_FILTERS,
      search: 'sword',
      sortBy: 'Relevance',
      sortDirection: 'DESC',
    }

    const result = await fetchAssetsPage(filters)

    const calledUrl = String((fetchMock.mock.calls[0] as unknown[])[0])
    expect(calledUrl).toContain('search=sword')
    expect(calledUrl).not.toContain('sortBy')
    expect(calledUrl).not.toContain('sortDirection')

    expect(result.isTruncated).toBe(true)
    expect(result.totalCount).toBe(3)
  })

  it('passes explicit sort params and defaults isTruncated to false when absent', async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response(JSON.stringify({ items: [assetRow], totalCount: 1, page: 1, pageSize: 12 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const filters: CatalogFilters = {
      ...DEFAULT_CATALOG_FILTERS,
      search: 'sword',
      sortBy: 'Price',
      sortDirection: 'ASC',
      page: 2,
    }

    const result = await fetchAssetsPage(filters)

    const calledUrl = String((fetchMock.mock.calls[0] as unknown[])[0])
    expect(calledUrl).toContain('sortBy=Price')
    expect(calledUrl).toContain('sortDirection=ASC')
    expect(calledUrl).toContain('page=2')

    expect(result.isTruncated).toBe(false)
  })
})
