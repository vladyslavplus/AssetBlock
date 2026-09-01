import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  getCatalogFacetsCached,
  getCatalogPageCached,
  getFeaturedAssetsCached,
} from '@/lib/server/catalog-server'
import type { CatalogFilters } from '@/lib/catalog/catalog-filters'

describe('catalog-server loaders', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  const dummyFilters: CatalogFilters = {
    page: 1,
    pageSize: 12,
    sortBy: 'CreatedAt',
    sortDirection: 'DESC',
    search: '',
    categoryId: '',
    tags: [],
    minPrice: null,
    maxPrice: null,
  }

  it('loads catalog page successfully from backend', async () => {
    const rawBackendData = {
      items: [
        {
          id: '11111111-1111-4111-8111-111111111111',
          title: '3D Sword',
          description: 'A sharp blade',
          price: 15,
          categoryId: '22222222-2222-4222-8222-222222222222',
          categoryName: 'Weapons',
          authorId: '33333333-3333-4333-8333-333333333333',
          authorUsername: 'blacksmith',
          createdAt: '2026-01-01T00:00:00Z',
          tags: ['weapon', 'sword'],
          averageRating: 4.8,
        },
      ],
      totalCount: 1,
      page: 1,
      pageSize: 12,
    }

    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify(rawBackendData), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    const result = await getCatalogPageCached(dummyFilters)
    expect(result).not.toBeNull()
    expect(result?.items).toHaveLength(1)
    expect(result?.items[0].title).toBe('3D Sword')
    expect(result?.totalCount).toBe(1)
  })

  it('returns null when catalog backend returns non-ok status', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('Internal Error', { status: 500 })),
    )

    const result = await getCatalogPageCached(dummyFilters)
    expect(result).toBeNull()
  })

  it('loads catalog facets (categories & tags) in parallel', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        if (url.includes('/api/categories')) {
          return new Response(
            JSON.stringify({
              items: [{ id: 'cat-1', name: 'Characters', slug: 'characters', description: null }],
              totalCount: 1,
            }),
            { status: 200 },
          )
        }
        if (url.includes('/api/tags')) {
          return new Response(
            JSON.stringify({
              items: [{ id: 'tag-1', name: 'fantasy' }],
              totalCount: 1,
            }),
            { status: 200 },
          )
        }
        return new Response('Not found', { status: 404 })
      }),
    )

    const facets = await getCatalogFacetsCached()
    expect(facets).not.toBeNull()
    expect(facets?.categories).toEqual([{ id: 'cat-1', name: 'Characters' }])
    expect(facets?.tags).toEqual(['fantasy'])
  })

  it('paginates and retrieves all categories and tags when totalCount > 100', async () => {
    const page1Categories = Array.from({ length: 100 }, (_, i) => ({
      id: `cat-${i}`,
      name: `Category ${i}`,
    }))
    const page2Categories = Array.from({ length: 20 }, (_, i) => ({
      id: `cat-${100 + i}`,
      name: `Category ${100 + i}`,
    }))

    const page1Tags = Array.from({ length: 100 }, (_, i) => ({
      id: `tag-${i}`,
      name: `tag-${i}`,
    }))
    const page2Tags = Array.from({ length: 15 }, (_, i) => ({
      id: `tag-${100 + i}`,
      name: `tag-${100 + i}`,
    }))

    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        const u = String(url)
        if (u.includes('/api/categories')) {
          if (u.includes('page=1')) {
            return new Response(JSON.stringify({ items: page1Categories, totalCount: 120 }), {
              status: 200,
            })
          }
          if (u.includes('page=2')) {
            return new Response(JSON.stringify({ items: page2Categories, totalCount: 120 }), {
              status: 200,
            })
          }
        }
        if (u.includes('/api/tags')) {
          if (u.includes('page=1')) {
            return new Response(JSON.stringify({ items: page1Tags, totalCount: 115 }), {
              status: 200,
            })
          }
          if (u.includes('page=2')) {
            return new Response(JSON.stringify({ items: page2Tags, totalCount: 115 }), {
              status: 200,
            })
          }
        }
        return new Response('Not found', { status: 404 })
      }),
    )

    const facets = await getCatalogFacetsCached()
    expect(facets).not.toBeNull()
    expect(facets?.categories).toHaveLength(120)
    expect(facets?.tags).toHaveLength(115)
    expect(facets?.categories[119].name).toBe('Category 119')
    expect(facets?.tags[114]).toBe('tag-114')
  })

  it('loads featured assets with limit', async () => {
    const rawItems = [
      {
        id: '11111111-1111-4111-8111-111111111111',
        title: 'Featured Pack',
        description: 'Best pack',
        price: 20,
        categoryId: '22222222-2222-4222-8222-222222222222',
        categoryName: 'Packs',
        authorId: '33333333-3333-4333-8333-333333333333',
        authorUsername: 'creator',
        createdAt: '2026-01-01T00:00:00Z',
        tags: ['pack'],
        averageRating: 5.0,
      },
    ]

    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ items: rawItems, totalCount: 1 }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    const featured = await getFeaturedAssetsCached(8)
    expect(featured).not.toBeNull()
    expect(featured).toHaveLength(1)
    expect(featured?.[0].title).toBe('Featured Pack')
  })
})
