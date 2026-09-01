import type { Route } from 'next'
import { normalizeAnalyticsReferrerHost } from '@/lib/analytics/analytics-referrer'
import {
  ANALYTICS_COLLECTION_ID_PARAM,
  ANALYTICS_SRC_PARAM,
  type AnalyticsSourceQuery,
  type AnalyticsTrafficSource,
} from '@/lib/analytics/telemetry-constants'

const SOURCE_QUERY_TO_TRAFFIC: Record<AnalyticsSourceQuery, AnalyticsTrafficSource> = {
  catalog: 'CATALOG',
  search: 'SEARCH',
  seller_profile: 'SELLER_PROFILE',
  collection: 'COLLECTION',
  bundle_page: 'BUNDLE_PAGE',
  direct_internal: 'DIRECT_INTERNAL',
  external: 'EXTERNAL',
  unknown: 'UNKNOWN',
}

export function mapSourceQueryToTrafficSource(value: string): AnalyticsTrafficSource | null {
  if (value in SOURCE_QUERY_TO_TRAFFIC) {
    return SOURCE_QUERY_TO_TRAFFIC[value as AnalyticsSourceQuery]
  }
  return null
}

export function buildAnalyticsQuery(
  source: AnalyticsSourceQuery,
  options?: { collectionId?: string },
): Record<string, string> {
  const params: Record<string, string> = { [ANALYTICS_SRC_PARAM]: source }
  if (options?.collectionId) {
    params[ANALYTICS_COLLECTION_ID_PARAM] = options.collectionId
  }
  return params
}

export function appendAnalyticsQuery(
  href: Route,
  source: AnalyticsSourceQuery,
  options?: { collectionId?: string },
): Route {
  const [path, existingQuery = ''] = href.split('?', 2)
  const params = new URLSearchParams(existingQuery)
  params.set(ANALYTICS_SRC_PARAM, source)
  if (options?.collectionId) {
    params.set(ANALYTICS_COLLECTION_ID_PARAM, options.collectionId)
  }
  const query = params.toString()
  return (query ? `${path}?${query}` : path) as Route
}

export function readCollectionIdFromSearchParams(
  searchParams: URLSearchParams | ReadonlyURLSearchParamsLike,
): string | undefined {
  const value = searchParams.get(ANALYTICS_COLLECTION_ID_PARAM)?.trim()
  if (!value) return undefined
  return value
}

export function resolveTrafficSourceFromLocation(
  searchParams: URLSearchParams | ReadonlyURLSearchParamsLike,
): AnalyticsTrafficSource {
  const srcParam = searchParams.get(ANALYTICS_SRC_PARAM)?.trim()
  if (srcParam) {
    const mapped = mapSourceQueryToTrafficSource(srcParam)
    if (mapped) return mapped
  }

  if (typeof document === 'undefined' || typeof window === 'undefined') {
    return 'UNKNOWN'
  }

  const referrer = document.referrer
  if (!referrer) {
    return 'UNKNOWN'
  }

  try {
    const refOrigin = new URL(referrer).origin
    if (refOrigin === window.location.origin) {
      return 'DIRECT_INTERNAL'
    }
    if (normalizeAnalyticsReferrerHost(referrer)) {
      return 'EXTERNAL'
    }
  } catch {
    // ignore malformed referrer
  }

  return 'UNKNOWN'
}

export function resolveReferrerHostFromDocument(): string | undefined {
  if (typeof document === 'undefined') return undefined
  const host = normalizeAnalyticsReferrerHost(document.referrer)
  return host ?? undefined
}

export interface CheckoutAttributionInput {
  source?: AnalyticsTrafficSource
  collectionId?: string
  referrerHost?: string
}

export function buildCheckoutAttributionFromPage(
  searchParams: URLSearchParams | ReadonlyURLSearchParamsLike,
): CheckoutAttributionInput {
  const source = resolveTrafficSourceFromLocation(searchParams)
  const collectionId = readCollectionIdFromSearchParams(searchParams)
  const referrerHost = source === 'EXTERNAL' ? resolveReferrerHostFromDocument() : undefined

  return {
    source,
    ...(collectionId ? { collectionId } : {}),
    ...(referrerHost ? { referrerHost } : {}),
  }
}

/** Preserves analytics query params on login return paths for purchase cards. */
export function buildPurchaseReturnPath(
  basePath: string,
  searchParams: URLSearchParams | ReadonlyURLSearchParamsLike,
): string {
  const params = new URLSearchParams()
  const src = searchParams.get(ANALYTICS_SRC_PARAM)?.trim()
  if (src) {
    params.set(ANALYTICS_SRC_PARAM, src)
  }
  const collectionId = searchParams.get(ANALYTICS_COLLECTION_ID_PARAM)?.trim()
  if (collectionId) {
    params.set(ANALYTICS_COLLECTION_ID_PARAM, collectionId)
  }
  const query = params.toString()
  return query ? `${basePath}?${query}` : basePath
}

interface ReadonlyURLSearchParamsLike {
  get(name: string): string | null
}
