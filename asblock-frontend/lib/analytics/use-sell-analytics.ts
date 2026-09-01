'use client'

import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query'
import type { Route } from 'next'
import { usePathname, useRouter, useSearchParams } from 'next/navigation'
import { useEffect } from 'react'

import { useAuth } from '@/components/auth/auth-context'
import { isEmailVerified } from '@/components/auth/email-verification-notice'
import { hasFullEngagementCoverage } from '@/lib/analytics/analytics-engagement'
import {
  analyticsSearchParamsEqual,
  buildAnalyticsCollectionsFilters,
  buildAnalyticsProductsFilters,
  canonicalizeAnalyticsState,
  maxAccessibleProductsPage,
  parseAnalyticsSearchParams,
  patchAnalyticsSearchParams,
  resolveAnalyticsUtcRange,
} from '@/lib/analytics/analytics-range'
import {
  sellerCollectionsQueryOptions,
  sellerOverviewQueryOptions,
  sellerProductsQueryOptions,
  sellerSalesInfiniteQueryOptions,
} from '@/lib/analytics/analytics-query'
import {
  ANALYTICS_DEFAULT_SALES_PAGE_SIZE,
  type AnalyticsUrlState,
} from '@/lib/analytics/analytics-types'

function buildAnalyticsRoute(
  pathname: string,
  searchParams: URLSearchParams,
  state: AnalyticsUrlState,
): Route {
  const patched = patchAnalyticsSearchParams(searchParams, state, 'analytics')
  const query = patched.toString()
  return (query ? `${pathname}?${query}` : pathname) as Route
}

export function useSellAnalyticsController() {
  const router = useRouter()
  const pathname = usePathname()
  const searchParams = useSearchParams()
  const { status, user } = useAuth()
  const authed = status === 'authenticated'
  const pending = status === 'loading'
  const verified = isEmailVerified(user)

  const currentParams = new URLSearchParams(searchParams.toString())
  const urlState = canonicalizeAnalyticsState(parseAnalyticsSearchParams(currentParams))
  const utcRange = resolveAnalyticsUtcRange(urlState)
  const productsFilters = buildAnalyticsProductsFilters(urlState)
  const collectionsFilters = buildAnalyticsCollectionsFilters(urlState)
  const salesFilters = {
    productType: urlState.productType,
    pageSize: ANALYTICS_DEFAULT_SALES_PAGE_SIZE,
  }

  useEffect(() => {
    const current = new URLSearchParams(searchParams.toString())
    const canonical = canonicalizeAnalyticsState(parseAnalyticsSearchParams(current))
    const patched = patchAnalyticsSearchParams(current, canonical)
    if (!analyticsSearchParamsEqual(current, patched)) {
      const query = patched.toString()
      router.replace((query ? `${pathname}?${query}` : pathname) as Route, { scroll: false })
    }
  }, [pathname, router, searchParams])

  const patchAnalyticsState = (patch: Partial<AnalyticsUrlState>) => {
    router.replace(
      buildAnalyticsRoute(
        pathname,
        new URLSearchParams(searchParams.toString()),
        canonicalizeAnalyticsState({ ...urlState, ...patch }),
      ),
      { scroll: false },
    )
  }

  const overviewQuery = useQuery({
    ...sellerOverviewQueryOptions(utcRange),
    enabled: authed && verified,
    placeholderData: keepPreviousData,
  })
  const productsQuery = useQuery({
    ...sellerProductsQueryOptions(utcRange, productsFilters),
    enabled: authed && verified,
    placeholderData: keepPreviousData,
  })
  const collectionsQuery = useQuery({
    ...sellerCollectionsQueryOptions(utcRange, collectionsFilters),
    enabled: authed && verified,
    placeholderData: keepPreviousData,
  })
  const salesQuery = useInfiniteQuery({
    ...sellerSalesInfiniteQueryOptions(utcRange, salesFilters),
    enabled: authed && verified,
  })

  const productsData = productsQuery.data
  const productsTotalPages =
    productsData && productsData.totalCount > 0
      ? Math.ceil(productsData.totalCount / productsData.pageSize)
      : 0
  useEffect(() => {
    if (!productsData || productsQuery.isPlaceholderData) return
    const pageSize = productsData.pageSize || productsFilters.pageSize
    const maxAccessible = maxAccessibleProductsPage(pageSize)
    let targetPage: number | null = null
    if (productsData.totalCount === 0 && urlState.page > 1) targetPage = 1
    else if (productsTotalPages > 0 && urlState.page > productsTotalPages) {
      targetPage = productsTotalPages
    } else if (urlState.page > maxAccessible) targetPage = maxAccessible
    if (targetPage == null || targetPage === urlState.page) return
    router.replace(
      buildAnalyticsRoute(
        pathname,
        new URLSearchParams(searchParams.toString()),
        canonicalizeAnalyticsState({ ...urlState, page: targetPage }),
      ),
      { scroll: false },
    )
  }, [
    pathname,
    productsData,
    productsFilters.pageSize,
    productsQuery.isPlaceholderData,
    productsTotalPages,
    router,
    searchParams,
    urlState,
  ])

  const collectionsData = collectionsQuery.data
  const collectionsTotalPages =
    collectionsData && collectionsData.totalCount > 0
      ? Math.ceil(collectionsData.totalCount / collectionsData.pageSize)
      : 0
  useEffect(() => {
    if (!collectionsData || collectionsQuery.isPlaceholderData) return
    const pageSize = collectionsData.pageSize || collectionsFilters.pageSize
    const maxAccessible = maxAccessibleProductsPage(pageSize)
    let targetPage: number | null = null
    if (collectionsData.totalCount === 0 && urlState.collectionPage > 1) targetPage = 1
    else if (collectionsTotalPages > 0 && urlState.collectionPage > collectionsTotalPages) {
      targetPage = collectionsTotalPages
    } else if (urlState.collectionPage > maxAccessible) targetPage = maxAccessible
    if (targetPage == null || targetPage === urlState.collectionPage) return
    router.replace(
      buildAnalyticsRoute(
        pathname,
        new URLSearchParams(searchParams.toString()),
        canonicalizeAnalyticsState({ ...urlState, collectionPage: targetPage }),
      ),
      { scroll: false },
    )
  }, [
    collectionsData,
    collectionsFilters.pageSize,
    collectionsQuery.isPlaceholderData,
    collectionsTotalPages,
    pathname,
    router,
    searchParams,
    urlState,
  ])

  useEffect(() => {
    if (!collectionsData || collectionsQuery.isPlaceholderData) return
    if (hasFullEngagementCoverage(collectionsData.engagementAvailableFrom, utcRange.from)) return
    if (
      urlState.collectionSort === 'ATTRIBUTED_REVENUE' &&
      urlState.collectionDirection === 'DESC'
    ) {
      return
    }
    router.replace(
      buildAnalyticsRoute(
        pathname,
        new URLSearchParams(searchParams.toString()),
        canonicalizeAnalyticsState({
          ...urlState,
          collectionSort: 'ATTRIBUTED_REVENUE',
          collectionDirection: 'DESC',
          collectionPage: 1,
        }),
      ),
      { scroll: false },
    )
  }, [
    collectionsData,
    collectionsQuery.isPlaceholderData,
    pathname,
    router,
    searchParams,
    utcRange.from,
    urlState,
  ])

  const salesItems = salesQuery.data?.pages.flatMap((page) => page.items) ?? []
  const overview = overviewQuery.data
  const overviewUpdating =
    overviewQuery.isFetching && (overviewQuery.isPlaceholderData || overviewQuery.isRefetching)
  const productsUpdating =
    productsQuery.isFetching && (productsQuery.isPlaceholderData || productsQuery.isRefetching)
  const collectionsUpdating =
    collectionsQuery.isFetching &&
    (collectionsQuery.isPlaceholderData || collectionsQuery.isRefetching)
  const salesUpdating = salesQuery.isFetching && !salesQuery.isFetchingNextPage

  return {
    authed,
    pending,
    verified,
    urlState,
    utcRange,
    patchAnalyticsState,
    overviewQuery,
    productsQuery,
    collectionsQuery,
    salesQuery,
    productsTotalPages,
    collectionsTotalPages,
    salesItems,
    salesCurrency: salesQuery.data?.pages[0]?.currency ?? overview?.currency ?? 'usd',
    hasMoreSales: salesQuery.data?.pages.at(-1)?.hasMore ?? false,
    overviewUpdating,
    productsUpdating,
    collectionsUpdating,
    salesUpdating,
    isUpdatingAnalytics:
      overviewUpdating || productsUpdating || collectionsUpdating || salesUpdating,
  }
}

export type SellAnalyticsController = ReturnType<typeof useSellAnalyticsController>
