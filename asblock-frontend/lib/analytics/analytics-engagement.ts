import { utcTodayDateOnly } from '@/lib/analytics/analytics-range-contract'

function parseTimestamp(value: string): number | null {
  const ms = Date.parse(value)
  return Number.isNaN(ms) ? null : ms
}

/** True when engagement telemetry exists for any portion of the selected API range. */
export function hasAnyEngagementCoverage(
  engagementAvailableFrom: string | null | undefined,
  rangeToExclusive: string,
): boolean {
  if (!engagementAvailableFrom) return false

  const availableFrom = parseTimestamp(engagementAvailableFrom)
  const rangeTo = parseTimestamp(`${rangeToExclusive}T00:00:00.000Z`)
  if (availableFrom === null || rangeTo === null) return false

  return availableFrom < rangeTo
}

/** True when engagement telemetry fully covers the selected range start (UTC). */
export function hasFullEngagementCoverage(
  engagementAvailableFrom: string | null | undefined,
  rangeFrom: string,
): boolean {
  if (!engagementAvailableFrom) return false

  const availableFrom = parseTimestamp(engagementAvailableFrom)
  const rangeFromMs = parseTimestamp(`${rangeFrom}T00:00:00.000Z`)
  if (availableFrom === null || rangeFromMs === null) return false

  return availableFrom <= rangeFromMs
}

/** True when the current UTC calendar day is included in the selected range. */
export function includesCurrentUtcDay(rangeFrom: string, rangeToExclusive: string): boolean {
  const today = utcTodayDateOnly()
  const from = parseTimestamp(`${rangeFrom}T00:00:00.000Z`)
  const to = parseTimestamp(`${rangeToExclusive}T00:00:00.000Z`)
  const todayDate = parseTimestamp(`${today}T00:00:00.000Z`)
  if (from === null || to === null || todayDate === null) return false

  return todayDate >= from && todayDate < to
}

export interface EngagementCountMetricLike {
  current: number
  previous: number | null
  absoluteChange: number | null
  percentageChange: number | null
}

/** Formats engagement count with nullable comparison semantics. */
export function engagementComparisonLabel(metric: EngagementCountMetricLike): string | null {
  const delta = metric.percentageChange
  if (delta != null) {
    const sign = delta > 0 ? '+' : ''
    return `${sign}${delta.toFixed(1)}% vs prior period`
  }
  if (metric.previous == null) {
    if (metric.current === 0) return 'No prior-period baseline'
    return 'Comparison unavailable (new baseline)'
  }
  return null
}

export function trafficSourceLabel(source: string): string {
  switch (source) {
    case 'CATALOG':
      return 'Catalog'
    case 'SEARCH':
      return 'Search'
    case 'SELLER_PROFILE':
      return 'Seller profile'
    case 'COLLECTION':
      return 'Collection'
    case 'BUNDLE_PAGE':
      return 'Bundle page'
    case 'DIRECT_INTERNAL':
      return 'Direct (internal)'
    case 'EXTERNAL':
      return 'External'
    case 'UNKNOWN':
      return 'Unknown'
    default:
      return source
  }
}

export function collectionSortLabel(sort: string): string {
  switch (sort) {
    case 'VIEWS':
      return 'Views'
    case 'CLICKS':
      return 'Clicks'
    case 'ATTRIBUTED_REVENUE':
      return 'Attributed revenue'
    case 'RECENT':
      return 'Recent activity'
    default:
      return sort
  }
}

export function collectionStatusLabel(status: string): string {
  switch (status) {
    case 'DRAFT':
      return 'Draft'
    case 'PUBLISHED':
      return 'Published'
    case 'ARCHIVED':
      return 'Archived'
    default:
      return status
  }
}
