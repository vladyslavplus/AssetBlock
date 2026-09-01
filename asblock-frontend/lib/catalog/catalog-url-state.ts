import {
  CATALOG_ASSETS_PAGE_SIZE,
  DEFAULT_CATALOG_FILTERS,
  sortDirectionForSortBy,
  type CatalogFilters,
} from '@/lib/catalog/catalog-filters'

type SearchParamsSource =
  | URLSearchParams
  | { get(name: string): string | null; getAll?(name: string): string[] }

const VALID_SORT_BY = new Set<CatalogFilters['sortBy']>(['CreatedAt', 'Title', 'Price'])
const VALID_SORT_DIR = new Set<CatalogFilters['sortDirection']>(['ASC', 'DESC'])
const POSITIVE_INT_RE = /^[1-9]\d*$/
const UUID_RE = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/

export function parsePositiveIntParam(raw: string | null | undefined, defaultValue = 1): number {
  if (!raw) return defaultValue
  const trimmed = raw.trim()
  if (!POSITIVE_INT_RE.test(trimmed)) return defaultValue
  const num = Number(trimmed)
  return Number.isSafeInteger(num) && num > 0 ? num : defaultValue
}

export function parseUuidParam(raw: string | null | undefined): string {
  if (!raw) return ''
  const trimmed = raw.trim()
  return UUID_RE.test(trimmed) ? trimmed.toLowerCase() : ''
}

export function parseCatalogUrlParams(sp: SearchParamsSource): CatalogFilters {
  const search = sp.get('search')?.trim() || sp.get('query')?.trim() || ''
  const categoryId = parseUuidParam(sp.get('categoryId') || sp.get('category'))

  let tags: string[] = []
  if (typeof sp.getAll === 'function') {
    const rawAll = sp.getAll('tags')
    if (rawAll.length > 0) {
      tags = rawAll
        .flatMap((t) => t.split(','))
        .map((t) => t.trim())
        .filter(Boolean)
    }
  }
  if (tags.length === 0) {
    const tagParam = sp.get('tags') || sp.get('tag')
    if (tagParam) {
      tags = tagParam
        .split(',')
        .map((t) => t.trim())
        .filter(Boolean)
    }
  }

  const rawMinPrice = sp.get('minPrice')
  const minPriceNum = rawMinPrice !== null && rawMinPrice !== '' ? Number(rawMinPrice) : null
  const minPrice =
    minPriceNum !== null && Number.isFinite(minPriceNum) && minPriceNum >= 0 ? minPriceNum : null

  const rawMaxPrice = sp.get('maxPrice')
  const maxPriceNum = rawMaxPrice !== null && rawMaxPrice !== '' ? Number(rawMaxPrice) : null
  const maxPrice =
    maxPriceNum !== null && Number.isFinite(maxPriceNum) && maxPriceNum >= 0 ? maxPriceNum : null

  const rawSortBy = sp.get('sortBy') as CatalogFilters['sortBy'] | null
  const sortBy: CatalogFilters['sortBy'] =
    rawSortBy && VALID_SORT_BY.has(rawSortBy) ? rawSortBy : DEFAULT_CATALOG_FILTERS.sortBy

  const rawSortDir = sp.get('sortDirection') as CatalogFilters['sortDirection'] | null
  const sortDirection: CatalogFilters['sortDirection'] =
    rawSortDir && VALID_SORT_DIR.has(rawSortDir) ? rawSortDir : sortDirectionForSortBy(sortBy)

  const page = parsePositiveIntParam(sp.get('page'), 1)

  return {
    search,
    categoryId,
    tags,
    minPrice,
    maxPrice,
    sortBy,
    sortDirection,
    page,
    pageSize: CATALOG_ASSETS_PAGE_SIZE,
  }
}

export function serializeCatalogUrlParams(filters: Partial<CatalogFilters>): URLSearchParams {
  const sp = new URLSearchParams()

  if (filters.search && filters.search.trim()) {
    sp.set('search', filters.search.trim())
  }

  const validCategory = parseUuidParam(filters.categoryId)
  if (validCategory) {
    sp.set('categoryId', validCategory)
  }

  if (filters.tags && filters.tags.length > 0) {
    sp.set('tags', filters.tags.join(','))
  }

  if (filters.minPrice !== null && filters.minPrice !== undefined && filters.minPrice > 0) {
    sp.set('minPrice', String(filters.minPrice))
  }

  if (filters.maxPrice !== null && filters.maxPrice !== undefined && filters.maxPrice > 0) {
    sp.set('maxPrice', String(filters.maxPrice))
  }

  if (filters.sortBy && filters.sortBy !== DEFAULT_CATALOG_FILTERS.sortBy) {
    sp.set('sortBy', filters.sortBy)
  }

  const expectedDir = sortDirectionForSortBy(filters.sortBy ?? DEFAULT_CATALOG_FILTERS.sortBy)
  if (filters.sortDirection && filters.sortDirection !== expectedDir) {
    sp.set('sortDirection', filters.sortDirection)
  }

  if (filters.page && filters.page > 1) {
    sp.set('page', String(filters.page))
  }

  return sp
}
