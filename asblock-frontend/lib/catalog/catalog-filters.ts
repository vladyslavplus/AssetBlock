export const CATALOG_ASSETS_PAGE_SIZE = 12

export type CatalogSortBy = 'CreatedAt' | 'Title' | 'Price'

export type UISortMode = CatalogSortBy | 'Relevance'

export interface CatalogFilters {
  search: string
  categoryId: string
  tags: string[]
  minPrice: number | null
  maxPrice: number | null
  sortBy: UISortMode
  sortDirection: 'ASC' | 'DESC'
  page: number
  pageSize: number
}

export const DEFAULT_CATALOG_FILTERS: CatalogFilters = {
  search: '',
  categoryId: '',
  tags: [],
  minPrice: null,
  maxPrice: null,
  sortBy: 'CreatedAt',
  sortDirection: 'DESC',
  page: 1,
  pageSize: CATALOG_ASSETS_PAGE_SIZE,
}

export function sortDirectionForSortBy(sortBy: CatalogSortBy): 'ASC' | 'DESC' {
  if (sortBy === 'CreatedAt') return 'DESC'
  return 'ASC'
}

const EXPLICIT_SORT_OPTIONS: Array<{ value: CatalogSortBy; label: string }> = [
  { value: 'CreatedAt', label: 'Newest' },
  { value: 'Title', label: 'Title A–Z' },
  { value: 'Price', label: 'Price: low to high' },
]

export const CATALOG_SORT_OPTIONS: Array<{ value: UISortMode; label: string }> = [
  { value: 'Relevance', label: 'Relevance' },
  ...EXPLICIT_SORT_OPTIONS,
]

/** Sort options available to the UI. Relevance is offered only for non-empty search. */
export function getCatalogSortOptions(
  hasSearch: boolean,
): Array<{ value: UISortMode; label: string }> {
  return hasSearch ? CATALOG_SORT_OPTIONS : EXPLICIT_SORT_OPTIONS
}

export function getCatalogSortLabel(sortBy: UISortMode): string {
  return CATALOG_SORT_OPTIONS.find((o) => o.value === sortBy)?.label ?? sortBy
}

/**
 * Derive the effective API sort params from UI filters.
 * Relevance mode: omit sortBy/sortDirection so the backend uses its relevance retrieval.
 * Explicit sort modes: pass through as-is.
 */
export function toApiSortParams(filters: CatalogFilters): {
  sortBy: CatalogSortBy
  sortDirection: 'ASC' | 'DESC'
} | null {
  if (filters.sortBy === 'Relevance') return null
  return { sortBy: filters.sortBy, sortDirection: filters.sortDirection }
}

/**
 * Clamp UI filter state so a cleared search never leaves a misleading relevance mode.
 * When search is empty and sortBy is Relevance (UI-only), fall back to the browse
 * default (CreatedAt DESC).
 */
export function deriveEffectiveSort(filters: CatalogFilters): CatalogFilters {
  const hasSearch = filters.search.trim().length > 0
  if (!hasSearch && filters.sortBy === 'Relevance') {
    return { ...filters, sortBy: 'CreatedAt', sortDirection: 'DESC' }
  }
  return filters
}

/**
 * For URL serialization: we never persist 'Relevance' in the URL.
 * If the user picked Relevance, omit sortBy/sortDirection from the URL.
 * If the user explicitly picked CreatedAt, always serialize it even if it's the browse default.
 */
export const UI_ONLY_SORT = 'Relevance' as const
