import { cache } from 'react'
import {
  buildAssetsQueryParams,
  mapApiAssetToListItem,
  type AssetListItemApi,
  type CategoryListItemApi,
  type FetchAssetsPageResult,
  type PagedResultDto,
  type TagDtoApi,
} from '@/lib/catalog/assets-api'
import type { AssetListItem } from '@/lib/catalog/asset-types'
import { CATALOG_ASSETS_PAGE_SIZE, type CatalogFilters } from '@/lib/catalog/catalog-filters'
import type { CatalogFacets } from '@/lib/catalog/catalog-query'
import { fetchBackendPublic } from '@/lib/server/fetch-backend'

export const DEFAULT_FEATURED_LIMIT = 8
const FACETS_PAGE_SIZE = 100
const MAX_FACET_PAGES = 50

async function readJson<T>(res: Response): Promise<T | undefined> {
  const text = await res.text()
  if (!text) return undefined
  try {
    return JSON.parse(text) as T
  } catch {
    return undefined
  }
}

async function fetchCategoriesServer(): Promise<Array<{ id: string; name: string }>> {
  const out: Array<{ id: string; name: string }> = []
  let page = 1
  while (page <= MAX_FACET_PAGES) {
    const params = new URLSearchParams({
      page: String(page),
      pageSize: String(FACETS_PAGE_SIZE),
      sortBy: 'Name',
      sortDirection: 'ASC',
    })
    const res = await fetchBackendPublic(`/api/categories?${params.toString()}`)
    if (!res.ok) {
      throw new Error(`Categories fetch failed: ${res.status}`)
    }
    const data = await readJson<PagedResultDto<CategoryListItemApi>>(res)
    if (!data) break
    const batch = data.items ?? []
    out.push(...batch.map((c) => ({ id: c.id, name: c.name })))
    if (out.length >= data.totalCount || batch.length < FACETS_PAGE_SIZE) break
    page += 1
  }
  return out
}

async function fetchTagsServer(): Promise<string[]> {
  const names: string[] = []
  let page = 1
  while (page <= MAX_FACET_PAGES) {
    const params = new URLSearchParams({
      page: String(page),
      pageSize: String(FACETS_PAGE_SIZE),
      sortBy: 'Name',
      sortDirection: 'ASC',
    })
    const res = await fetchBackendPublic(`/api/tags?${params.toString()}`)
    if (!res.ok) {
      throw new Error(`Tags fetch failed: ${res.status}`)
    }
    const data = await readJson<PagedResultDto<TagDtoApi>>(res)
    if (!data) break
    const batch = data.items ?? []
    for (const item of batch) {
      const trimmed = item.name?.trim()
      if (trimmed) names.push(trimmed)
    }
    if (names.length >= data.totalCount || batch.length < FACETS_PAGE_SIZE) break
    page += 1
  }
  return names
}

/**
 * Server-side loader for the assets catalog with request-scoped caching.
 * Returns null on upstream failure so Client Component can fall back to client queries and retries.
 */
export const getCatalogPageCached = cache(
  async (filters: CatalogFilters): Promise<FetchAssetsPageResult | null> => {
    try {
      const qs = buildAssetsQueryParams(filters)
      const res = await fetchBackendPublic(`/api/assets?${qs}`)
      if (!res.ok) {
        return null
      }
      const data = await readJson<PagedResultDto<AssetListItemApi>>(res)
      if (!data) return null
      const totalPages =
        CATALOG_ASSETS_PAGE_SIZE > 0 ? Math.ceil(data.totalCount / CATALOG_ASSETS_PAGE_SIZE) : 0
      return {
        items: (data.items ?? []).map(mapApiAssetToListItem),
        totalCount: data.totalCount,
        page: data.page,
        pageSize: CATALOG_ASSETS_PAGE_SIZE,
        totalPages,
      }
    } catch {
      return null
    }
  },
)

/**
 * Server-side loader for catalog facets (categories & tags) in parallel with request-scoped caching.
 * Performs bounded pagination to retrieve all facets across pages.
 */
export const getCatalogFacetsCached = cache(async (): Promise<CatalogFacets | null> => {
  try {
    const [categories, tags] = await Promise.all([fetchCategoriesServer(), fetchTagsServer()])
    return { categories, tags }
  } catch {
    return null
  }
})

/**
 * Server-side loader for marketing featured assets with request-scoped caching.
 */
export const getFeaturedAssetsCached = cache(
  async (limit = DEFAULT_FEATURED_LIMIT): Promise<AssetListItem[] | null> => {
    try {
      const params = new URLSearchParams({
        page: '1',
        pageSize: String(limit),
        sortBy: 'CreatedAt',
        sortDirection: 'DESC',
      })
      const res = await fetchBackendPublic(`/api/assets?${params.toString()}`)
      if (!res.ok) {
        return null
      }
      const data = await readJson<PagedResultDto<AssetListItemApi>>(res)
      if (!data) return null
      return (data.items ?? []).map(mapApiAssetToListItem)
    } catch {
      return null
    }
  },
)
