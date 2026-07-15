import {
  fetchAssetsPage,
  fetchCategoryOptions,
  fetchFeaturedAssets,
  fetchTagNamesForFilters,
  type FetchAssetsPageResult,
} from '@/lib/catalog/assets-api'
import type { CatalogFilters } from '@/lib/catalog/catalog-filters'

export const catalogKeys = {
  all: ['catalog'] as const,
  facets: () => [...catalogKeys.all, 'facets'] as const,
  list: (filters: CatalogFilters) => [...catalogKeys.all, 'list', filters] as const,
  featured: (limit: number) => [...catalogKeys.all, 'featured', limit] as const,
}

export interface CatalogFacets {
  categories: Array<{ id: string; name: string }>
  tags: string[]
}

export async function fetchCatalogFacets({
  signal,
}: { signal?: AbortSignal } = {}): Promise<CatalogFacets> {
  const [categories, tags] = await Promise.all([
    fetchCategoryOptions({ signal }),
    fetchTagNamesForFilters({ signal }),
  ])
  return { categories, tags }
}

export async function fetchCatalogPage(
  filters: CatalogFilters,
  signal?: AbortSignal,
): Promise<FetchAssetsPageResult> {
  return fetchAssetsPage(filters, { signal })
}

export { fetchFeaturedAssets }
