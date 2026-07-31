import 'server-only'

import { isValidAnalyticsRange } from '@/lib/analytics/analytics-range-contract'
import {
  ANALYTICS_COLLECTION_SORTS,
  ANALYTICS_DEFAULT_PRODUCTS_PAGE_SIZE,
  ANALYTICS_MAX_CURSOR_LENGTH,
  ANALYTICS_MAX_PAGE_SIZE,
  ANALYTICS_MAX_PRODUCTS_OFFSET,
  ANALYTICS_MAX_PRODUCTS_PAGE,
  ANALYTICS_PRODUCT_SORTS,
  ANALYTICS_PRODUCT_TYPE_FILTERS,
  ANALYTICS_SORT_DIRECTIONS,
} from '@/lib/analytics/analytics-types'
import { problemResponse } from '@/lib/server/bff-http'

const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/

const PRODUCT_TYPES = new Set<string>(ANALYTICS_PRODUCT_TYPE_FILTERS)
const SORTS = new Set<string>(ANALYTICS_PRODUCT_SORTS)
const COLLECTION_SORTS = new Set<string>(ANALYTICS_COLLECTION_SORTS)
const DIRECTIONS = new Set<string>(ANALYTICS_SORT_DIRECTIONS)

function isDateOnly(value: string): boolean {
  if (!DATE_ONLY.test(value)) return false
  const [y, m, d] = value.split('-').map(Number)
  const parsed = new Date(Date.UTC(y, m - 1, d))
  return (
    parsed.getUTCFullYear() === y && parsed.getUTCMonth() === m - 1 && parsed.getUTCDate() === d
  )
}

function isPositiveInt(value: string): boolean {
  if (!/^\d+$/.test(value)) return false
  const n = Number(value)
  return Number.isSafeInteger(n) && n >= 1
}

export type AnalyticsBffQueryResult = { ok: true; qs: string } | { ok: false; response: Response }

type OptionalDateResult = { ok: true; value: string | null } | { ok: false; response: Response }

function fail(detail: string, field: string): { ok: false; response: Response } {
  return {
    ok: false,
    response: problemResponse(400, 'ERR_VALIDATION_FAILED', detail, {
      [field]: [detail],
    }),
  }
}

function optionalDate(url: URL, key: 'from' | 'to'): OptionalDateResult {
  const value = url.searchParams.get(key)
  if (value === null || value === '') return { ok: true, value: null }
  if (!isDateOnly(value)) {
    return fail(`${key} must be a valid UTC date (YYYY-MM-DD).`, key)
  }
  return { ok: true, value }
}

function validateRangePair(
  from: string | null,
  to: string | null,
): { ok: true } | { ok: false; response: Response } {
  if (!from || !to) return { ok: true }
  if (!isValidAnalyticsRange(from, to)) {
    return fail(
      'from/to must satisfy from < to, at most 366 days, and to not after tomorrow UTC.',
      'to',
    )
  }
  return { ok: true }
}

function buildQs(entries: Array<[string, string]>): string {
  const out = new URLSearchParams()
  for (const [key, value] of entries) {
    out.set(key, value)
  }
  const qs = out.toString()
  return qs ? `?${qs}` : ''
}

function validateProductsPagination(
  page: string | null,
  pageSize: string | null,
): { ok: true; pageNum: number; pageSizeNum: number } | { ok: false; response: Response } {
  const pageNum = page ? Number.parseInt(page, 10) : 1
  const pageSizeNum = pageSize
    ? Number.parseInt(pageSize, 10)
    : ANALYTICS_DEFAULT_PRODUCTS_PAGE_SIZE

  if (page && !isPositiveInt(page)) {
    return fail('page must be a positive integer.', 'page')
  }
  if (pageSize && !isPositiveInt(pageSize)) {
    return fail('pageSize must be a positive integer.', 'pageSize')
  }
  if (pageNum > ANALYTICS_MAX_PRODUCTS_PAGE) {
    return fail(`page must be at most ${ANALYTICS_MAX_PRODUCTS_PAGE}.`, 'page')
  }
  if (pageSizeNum > ANALYTICS_MAX_PAGE_SIZE) {
    return fail(`pageSize must be at most ${ANALYTICS_MAX_PAGE_SIZE}.`, 'pageSize')
  }

  const offset = BigInt(pageNum - 1) * BigInt(pageSizeNum)
  if (offset > BigInt(ANALYTICS_MAX_PRODUCTS_OFFSET)) {
    return fail(`Pagination offset exceeds maximum of ${ANALYTICS_MAX_PRODUCTS_OFFSET}.`, 'page')
  }

  return { ok: true, pageNum, pageSizeNum }
}

export function analyticsOverviewBackendQuery(url: URL): AnalyticsBffQueryResult {
  const from = optionalDate(url, 'from')
  if (!from.ok) return from
  const to = optionalDate(url, 'to')
  if (!to.ok) return to

  const rangePair = validateRangePair(from.value, to.value)
  if (!rangePair.ok) return rangePair

  const entries: Array<[string, string]> = []
  if (from.value) entries.push(['from', from.value])
  if (to.value) entries.push(['to', to.value])
  return { ok: true, qs: buildQs(entries) }
}

export function analyticsProductsBackendQuery(url: URL): AnalyticsBffQueryResult {
  const from = optionalDate(url, 'from')
  if (!from.ok) return from
  const to = optionalDate(url, 'to')
  if (!to.ok) return to

  const rangePair = validateRangePair(from.value, to.value)
  if (!rangePair.ok) return rangePair

  const entries: Array<[string, string]> = []
  if (from.value) entries.push(['from', from.value])
  if (to.value) entries.push(['to', to.value])

  const productType = url.searchParams.get('productType')
  let normalizedProductType: string | null = null
  if (productType) {
    const normalized = productType.toUpperCase()
    if (!PRODUCT_TYPES.has(normalized)) {
      return fail('productType must be ALL, ASSET, or BUNDLE.', 'productType')
    }
    normalizedProductType = normalized
    entries.push(['productType', normalized])
  }

  const sort = url.searchParams.get('sort')
  let normalizedSort: string | null = null
  if (sort) {
    const normalized = sort.toUpperCase()
    if (!SORTS.has(normalized)) {
      return fail('sort must be one of REVENUE, ORDERS, UNITS, RATING, RECENT.', 'sort')
    }
    normalizedSort = normalized
    entries.push(['sort', normalized])
  }

  if (normalizedProductType === 'BUNDLE' && normalizedSort === 'RATING') {
    return fail('RATING sort is not supported for BUNDLE product type.', 'sort')
  }

  const direction = url.searchParams.get('direction')
  if (direction) {
    const normalized = direction.toUpperCase()
    if (!DIRECTIONS.has(normalized)) {
      return fail('direction must be ASC or DESC.', 'direction')
    }
    entries.push(['direction', normalized])
  }

  const page = url.searchParams.get('page')
  const pageSize = url.searchParams.get('pageSize')
  const pagination = validateProductsPagination(page, pageSize)
  if (!pagination.ok) return pagination

  if (page) entries.push(['page', page])
  if (pageSize) entries.push(['pageSize', pageSize])

  return { ok: true, qs: buildQs(entries) }
}

export function analyticsSalesBackendQuery(url: URL): AnalyticsBffQueryResult {
  const from = optionalDate(url, 'from')
  if (!from.ok) return from
  const to = optionalDate(url, 'to')
  if (!to.ok) return to

  const rangePair = validateRangePair(from.value, to.value)
  if (!rangePair.ok) return rangePair

  const entries: Array<[string, string]> = []
  if (from.value) entries.push(['from', from.value])
  if (to.value) entries.push(['to', to.value])

  const productType = url.searchParams.get('productType')
  if (productType) {
    const normalized = productType.toUpperCase()
    if (!PRODUCT_TYPES.has(normalized)) {
      return fail('productType must be ALL, ASSET, or BUNDLE.', 'productType')
    }
    entries.push(['productType', normalized])
  }

  const cursor = url.searchParams.get('cursor')
  if (cursor) {
    if (cursor.length > ANALYTICS_MAX_CURSOR_LENGTH) {
      return fail(`cursor must be at most ${ANALYTICS_MAX_CURSOR_LENGTH} characters.`, 'cursor')
    }
    entries.push(['cursor', cursor])
  }

  const pageSize = url.searchParams.get('pageSize')
  if (pageSize) {
    if (!isPositiveInt(pageSize)) {
      return fail('pageSize must be a positive integer.', 'pageSize')
    }
    const pageSizeNum = Number.parseInt(pageSize, 10)
    if (pageSizeNum > ANALYTICS_MAX_PAGE_SIZE) {
      return fail(`pageSize must be at most ${ANALYTICS_MAX_PAGE_SIZE}.`, 'pageSize')
    }
    entries.push(['pageSize', pageSize])
  }

  return { ok: true, qs: buildQs(entries) }
}

function parseRequiredDateRange(url: URL): AnalyticsBffQueryResult {
  const from = optionalDate(url, 'from')
  if (!from.ok) return from
  const to = optionalDate(url, 'to')
  if (!to.ok) return to

  if (!from.value || !to.value) {
    return fail('from and to are required.', 'from')
  }

  const rangePair = validateRangePair(from.value, to.value)
  if (!rangePair.ok) return rangePair

  return {
    ok: true,
    qs: buildQs([
      ['from', from.value],
      ['to', to.value],
    ]),
  }
}

function appendOptionalProductType(
  url: URL,
  entries: Array<[string, string]>,
): AnalyticsBffQueryResult {
  const productType = url.searchParams.get('productType')
  if (!productType) return { ok: true, qs: buildQs(entries) }

  const normalized = productType.toUpperCase()
  if (!PRODUCT_TYPES.has(normalized)) {
    return fail('productType must be ALL, ASSET, or BUNDLE.', 'productType')
  }
  entries.push(['productType', normalized])
  return { ok: true, qs: buildQs(entries) }
}

export function analyticsSalesExportBackendQuery(url: URL): AnalyticsBffQueryResult {
  const range = parseRequiredDateRange(url)
  if (!range.ok) return range

  const entries: Array<[string, string]> = []
  const from = url.searchParams.get('from')
  const to = url.searchParams.get('to')
  if (from) entries.push(['from', from])
  if (to) entries.push(['to', to])

  return appendOptionalProductType(url, entries)
}

export function analyticsCollectionsBackendQuery(url: URL): AnalyticsBffQueryResult {
  const from = optionalDate(url, 'from')
  if (!from.ok) return from
  const to = optionalDate(url, 'to')
  if (!to.ok) return to

  const rangePair = validateRangePair(from.value, to.value)
  if (!rangePair.ok) return rangePair

  const entries: Array<[string, string]> = []
  if (from.value) entries.push(['from', from.value])
  if (to.value) entries.push(['to', to.value])

  const sort = url.searchParams.get('sort')
  if (sort) {
    const normalized = sort.toUpperCase()
    if (!COLLECTION_SORTS.has(normalized)) {
      return fail('sort must be one of VIEWS, CLICKS, ATTRIBUTED_REVENUE, RECENT.', 'sort')
    }
    entries.push(['sort', normalized])
  }

  const direction = url.searchParams.get('direction')
  if (direction) {
    const normalized = direction.toUpperCase()
    if (!DIRECTIONS.has(normalized)) {
      return fail('direction must be ASC or DESC.', 'direction')
    }
    entries.push(['direction', normalized])
  }

  const page = url.searchParams.get('page')
  const pageSize = url.searchParams.get('pageSize')
  const pagination = validateProductsPagination(page, pageSize)
  if (!pagination.ok) return pagination

  if (page) entries.push(['page', page])
  if (pageSize) entries.push(['pageSize', pageSize])

  return { ok: true, qs: buildQs(entries) }
}

export function analyticsProductDetailBackendQuery(url: URL): AnalyticsBffQueryResult {
  return analyticsOverviewBackendQuery(url)
}
