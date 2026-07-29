import {
  ANALYTICS_DEFAULT_PRODUCTS_PAGE_SIZE,
  ANALYTICS_MAX_DAYS,
  ANALYTICS_MAX_PRODUCTS_OFFSET,
  ANALYTICS_MAX_PRODUCTS_PAGE,
  ANALYTICS_RANGE_PRESETS,
  type AnalyticsProductSort,
  type AnalyticsProductTypeFilter,
  type AnalyticsRangePreset,
  type AnalyticsSortDirection,
  type AnalyticsUrlState,
  type AnalyticsUtcRange,
} from '@/lib/analytics/analytics-types'
import {
  analyticsProductSortSchema,
  analyticsProductTypeFilterSchema,
  analyticsSortDirectionSchema,
} from '@/lib/analytics/analytics-schemas'
import { parseDateOnlyUtc } from '@/lib/analytics/analytics-format'
import {
  addUtcDays,
  dayNumberUtc,
  isValidAnalyticsRange,
  utcTodayDateOnly,
} from '@/lib/analytics/analytics-range-contract'

const ANALYTICS_URL_KEYS = [
  'range',
  'from',
  'to',
  'productType',
  'sort',
  'direction',
  'page',
  'tab',
] as const

/** API `to` is exclusive; display end is inclusive (`to - 1 day`). */
export function displayInclusiveEndFromApiTo(apiTo: string): string {
  return addUtcDays(apiTo, -1)
}

/** Custom UI end date (inclusive) → exclusive API `to`. */
export function apiToFromInclusiveEnd(inclusiveEnd: string): string {
  return addUtcDays(inclusiveEnd, 1)
}

export function resolvePresetRange(preset: AnalyticsRangePreset): AnalyticsUtcRange {
  const today = utcTodayDateOnly()
  const apiTo = addUtcDays(today, 1)

  switch (preset) {
    case '7d':
      return { from: addUtcDays(today, -6), to: apiTo }
    case '30d':
      return { from: addUtcDays(today, -29), to: apiTo }
    case '90d':
      return { from: addUtcDays(today, -89), to: apiTo }
    case 'ytd': {
      const year = today.slice(0, 4)
      return { from: `${year}-01-01`, to: apiTo }
    }
    case 'custom':
      return resolvePresetRange('30d')
  }
}

export function validateCustomInclusiveRange(
  from: string,
  toInclusive: string,
): { ok: true } | { ok: false; message: string } {
  const fromParsed = parseDateOnlyUtc(from)
  const toParsed = parseDateOnlyUtc(toInclusive)
  if (!fromParsed || !toParsed) {
    return { ok: false, message: 'Enter valid UTC dates (YYYY-MM-DD).' }
  }

  const fromDay = dayNumberUtc(from)
  const toDay = dayNumberUtc(toInclusive)
  if (fromDay == null || toDay == null) {
    return { ok: false, message: 'Enter valid UTC dates (YYYY-MM-DD).' }
  }
  if (toDay < fromDay) {
    return { ok: false, message: 'End date must be on or after the start date.' }
  }

  const apiTo = apiToFromInclusiveEnd(toInclusive)
  if (!isValidAnalyticsRange(from, apiTo)) {
    return {
      ok: false,
      message: `Custom range cannot exceed ${ANALYTICS_MAX_DAYS} days or extend beyond today (UTC).`,
    }
  }

  return { ok: true }
}

export function resolveAnalyticsUtcRange(state: AnalyticsUrlState): AnalyticsUtcRange {
  if (state.range === 'custom') {
    if (state.customFrom && state.customTo) {
      const apiTo = apiToFromInclusiveEnd(state.customTo)
      if (isValidAnalyticsRange(state.customFrom, apiTo)) {
        return { from: state.customFrom, to: apiTo }
      }
    }
  } else if ((ANALYTICS_RANGE_PRESETS as readonly string[]).includes(state.range)) {
    return resolvePresetRange(state.range)
  }

  return resolvePresetRange('30d')
}

function parseRangePreset(value: string | null): AnalyticsRangePreset {
  if (value && (ANALYTICS_RANGE_PRESETS as readonly string[]).includes(value)) {
    return value as AnalyticsRangePreset
  }
  return '30d'
}

function parsePositiveInt(value: string | null, fallback: number): number {
  if (!value) return fallback
  if (!/^\d+$/.test(value)) return fallback
  const parsed = Number(value)
  if (!Number.isSafeInteger(parsed) || parsed < 1) return fallback
  return parsed
}

/** Highest products page that stays within BFF page and offset caps. */
export function maxAccessibleProductsPage(
  pageSize: number = ANALYTICS_DEFAULT_PRODUCTS_PAGE_SIZE,
): number {
  const safePageSize = Math.max(1, pageSize)
  const maxByOffset = Math.floor(ANALYTICS_MAX_PRODUCTS_OFFSET / safePageSize) + 1
  return Math.min(ANALYTICS_MAX_PRODUCTS_PAGE, maxByOffset)
}

/** Lenient parse of known analytics URL keys without normalization. */
export function parseAnalyticsSearchParams(params: URLSearchParams): AnalyticsUrlState {
  const range = parseRangePreset(params.get('range'))
  const customFrom = params.get('from')
  const customTo = params.get('to')

  const productTypeParsed = analyticsProductTypeFilterSchema.safeParse(
    params.get('productType')?.toUpperCase(),
  )
  const sortParsed = analyticsProductSortSchema.safeParse(params.get('sort')?.toUpperCase())
  const directionParsed = analyticsSortDirectionSchema.safeParse(
    params.get('direction')?.toUpperCase(),
  )

  return {
    range,
    customFrom: customFrom && parseDateOnlyUtc(customFrom) ? customFrom : null,
    customTo: customTo && parseDateOnlyUtc(customTo) ? customTo : null,
    productType: productTypeParsed.success ? productTypeParsed.data : 'ALL',
    sort: sortParsed.success ? sortParsed.data : 'REVENUE',
    direction: directionParsed.success ? directionParsed.data : 'DESC',
    page: parsePositiveInt(params.get('page'), 1),
  }
}

/** Canonicalize invalid analytics URL state. */
export function canonicalizeAnalyticsState(state: AnalyticsUrlState): AnalyticsUrlState {
  let next: AnalyticsUrlState = { ...state }

  if (next.range === 'custom') {
    if (!next.customFrom || !next.customTo) {
      next = { ...next, range: '30d', customFrom: null, customTo: null }
    } else {
      const apiTo = apiToFromInclusiveEnd(next.customTo)
      if (!isValidAnalyticsRange(next.customFrom, apiTo)) {
        next = { ...next, range: '30d', customFrom: null, customTo: null }
      }
    }
  } else if (!(ANALYTICS_RANGE_PRESETS as readonly string[]).includes(next.range)) {
    next = { ...next, range: '30d', customFrom: null, customTo: null }
  } else {
    next = { ...next, customFrom: null, customTo: null }
  }

  if (!analyticsProductTypeFilterSchema.safeParse(next.productType).success) {
    next = { ...next, productType: 'ALL' }
  }
  if (!analyticsProductSortSchema.safeParse(next.sort).success) {
    next = { ...next, sort: 'REVENUE' }
  }
  if (!analyticsSortDirectionSchema.safeParse(next.direction).success) {
    next = { ...next, direction: 'DESC' }
  }

  const maxPage = maxAccessibleProductsPage(ANALYTICS_DEFAULT_PRODUCTS_PAGE_SIZE)
  if (next.page < 1) {
    next = { ...next, page: 1 }
  } else if (next.page > maxPage) {
    next = { ...next, page: maxPage }
  }

  if (next.productType === 'BUNDLE' && next.sort === 'RATING') {
    next = { ...next, sort: 'REVENUE', direction: 'DESC', page: 1 }
  }

  return next
}

function deleteAnalyticsKeys(params: URLSearchParams): void {
  for (const key of ANALYTICS_URL_KEYS) {
    if (key !== 'tab') {
      params.delete(key)
    }
  }
}

function serializeAnalyticsState(params: URLSearchParams, state: AnalyticsUrlState): void {
  if (state.range === 'custom' && state.customFrom && state.customTo) {
    params.set('range', 'custom')
    params.set('from', state.customFrom)
    params.set('to', state.customTo)
  } else if (state.range !== '30d') {
    params.set('range', state.range)
  }

  if (state.productType !== 'ALL') {
    params.set('productType', state.productType)
  }
  if (state.sort !== 'REVENUE') {
    params.set('sort', state.sort)
  }
  if (state.direction !== 'DESC') {
    params.set('direction', state.direction)
  }
  if (state.page > 1) {
    params.set('page', String(state.page))
  }
}

/** Patch known analytics keys into a copy of current params; preserves unrelated keys. */
export function patchAnalyticsSearchParams(
  current: URLSearchParams,
  state: AnalyticsUrlState,
  tab?: 'analytics',
): URLSearchParams {
  const params = new URLSearchParams(current.toString())
  deleteAnalyticsKeys(params)
  serializeAnalyticsState(params, state)

  if (tab === 'analytics') {
    params.set('tab', 'analytics')
  }

  return params
}

export function analyticsSearchParamsEqual(a: URLSearchParams, b: URLSearchParams): boolean {
  for (const key of ANALYTICS_URL_KEYS) {
    if (a.get(key) !== b.get(key)) return false
  }
  return true
}

export function buildAnalyticsProductsFilters(state: AnalyticsUrlState) {
  return {
    productType: state.productType as AnalyticsProductTypeFilter,
    sort: state.sort as AnalyticsProductSort,
    direction: state.direction as AnalyticsSortDirection,
    page: state.page,
    pageSize: ANALYTICS_DEFAULT_PRODUCTS_PAGE_SIZE,
  }
}

export function rangePresetLabel(preset: AnalyticsRangePreset): string {
  switch (preset) {
    case '7d':
      return 'Last 7 days'
    case '30d':
      return 'Last 30 days'
    case '90d':
      return 'Last 90 days'
    case 'ytd':
      return 'Year to date'
    case 'custom':
      return 'Custom'
  }
}

export function formatAnalyticsRangeLabel(
  state: AnalyticsUrlState,
  utcRange: AnalyticsUtcRange,
): string {
  if (state.range === 'custom' && state.customFrom && state.customTo) {
    return `${state.customFrom} – ${state.customTo} UTC`
  }
  const inclusiveEnd = displayInclusiveEndFromApiTo(utcRange.to)
  return `${utcRange.from} – ${inclusiveEnd} UTC`
}
