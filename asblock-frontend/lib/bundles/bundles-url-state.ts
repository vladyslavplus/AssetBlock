import { parsePositiveIntParam } from '@/lib/catalog/catalog-url-state'

type SearchParamsSource = URLSearchParams | { get(name: string): string | null }

export interface BundlesBrowseFilters {
  search: string
  page: number
}

export function parseBundlesUrlParams(sp: SearchParamsSource): BundlesBrowseFilters {
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
