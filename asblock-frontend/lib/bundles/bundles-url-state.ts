import {
  normalizeSearchParamsSource,
  parsePositiveIntParam,
  type SearchParamsSource,
} from '@/lib/catalog/catalog-url-state'

export interface BundlesBrowseFilters {
  search: string
  page: number
}

export function parseBundlesUrlParams(rawSource: SearchParamsSource): BundlesBrowseFilters {
  const sp = normalizeSearchParamsSource(rawSource)
  const search = sp.get('search')?.trim() || sp.get('query')?.trim() || ''
  const page = parsePositiveIntParam(sp.get('page'), 1)

  return { search, page }
}

export function serializeBundlesUrlParams(filters: Partial<BundlesBrowseFilters>): URLSearchParams {
  const sp = new URLSearchParams()
  if (filters.search && filters.search.trim()) {
    sp.set('search', filters.search.trim())
  }
  if (filters.page && filters.page > 1) {
    sp.set('page', String(filters.page))
  }
  return sp
}
