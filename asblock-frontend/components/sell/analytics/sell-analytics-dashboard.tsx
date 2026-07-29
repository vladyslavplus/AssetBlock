'use client'

import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { usePathname, useRouter, useSearchParams } from 'next/navigation'
import { useEffect } from 'react'

import { useAuth } from '@/components/auth/auth-context'
import {
  EmailVerificationNotice,
  isEmailVerified,
} from '@/components/auth/email-verification-notice'
import { AnalyticsKpiCards } from '@/components/sell/analytics/analytics-kpi-cards'
import { AnalyticsProductsTable } from '@/components/sell/analytics/analytics-products-table'
import { AnalyticsRangePicker } from '@/components/sell/analytics/analytics-range-picker'
import { AnalyticsRecentSales } from '@/components/sell/analytics/analytics-recent-sales'
import { AnalyticsRevenueSplit } from '@/components/sell/analytics/analytics-revenue-split'
import { AnalyticsSectionError } from '@/components/sell/analytics/analytics-section-error'
import {
  AnalyticsChartSkeleton,
  AnalyticsKpiSkeletonGrid,
  AnalyticsListSkeleton,
  AnalyticsTableSkeleton,
} from '@/components/sell/analytics/analytics-skeletons'
import { AnalyticsSeriesChart } from '@/components/sell/analytics/analytics-series-chart'
import {
  AnalyticsNoProductsNotice,
  AnalyticsNoSalesNotice,
  AnalyticsTopProducts,
} from '@/components/sell/analytics/analytics-top-products'
import { formatUtcDateTime } from '@/lib/analytics/analytics-format'
import {
  analyticsSearchParamsEqual,
  buildAnalyticsProductsFilters,
  canonicalizeAnalyticsState,
  maxAccessibleProductsPage,
  parseAnalyticsSearchParams,
  patchAnalyticsSearchParams,
  resolveAnalyticsUtcRange,
} from '@/lib/analytics/analytics-range'
import {
  sellerOverviewQueryOptions,
  sellerProductsQueryOptions,
  sellerSalesInfiniteQueryOptions,
} from '@/lib/analytics/analytics-query'
import {
  ANALYTICS_DEFAULT_SALES_PAGE_SIZE,
  type AnalyticsUrlState,
} from '@/lib/analytics/analytics-types'
import { ApiRequestError } from '@/lib/http/api-client'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'

function queryErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof ApiRequestError) {
    if (error.status === 401) {
      return 'Please sign in to view analytics.'
    }
    if (error.status === 403) {
      return 'Verify your email address to view seller analytics.'
    }
    return error.message || fallback
  }
  if (error instanceof Error) {
    return error.message
  }
  return fallback
}

export function SellAnalyticsDashboard() {
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
  const salesFilters = {
    productType: urlState.productType,
    pageSize: ANALYTICS_DEFAULT_SALES_PAGE_SIZE,
  }

  useEffect(() => {
    const current = new URLSearchParams(searchParams.toString())
    const canonical = canonicalizeAnalyticsState(parseAnalyticsSearchParams(current))
    const patched = patchAnalyticsSearchParams(current, canonical)
    if (!analyticsSearchParamsEqual(current, patched)) {
      const qs = patched.toString()
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
    }
  }, [pathname, router, searchParams])

  function patchAnalyticsState(patch: Partial<AnalyticsUrlState>) {
    const current = new URLSearchParams(searchParams.toString())
    const next = canonicalizeAnalyticsState({ ...urlState, ...patch })
    const patched = patchAnalyticsSearchParams(current, next, 'analytics')
    const qs = patched.toString()
    router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
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

    if (productsData.totalCount === 0 && urlState.page > 1) {
      targetPage = 1
    } else if (productsTotalPages > 0 && urlState.page > productsTotalPages) {
      targetPage = productsTotalPages
    } else if (urlState.page > maxAccessible) {
      targetPage = maxAccessible
    }

    if (targetPage == null || targetPage === urlState.page) return

    const current = new URLSearchParams(searchParams.toString())
    const next = canonicalizeAnalyticsState({ ...urlState, page: targetPage })
    const patched = patchAnalyticsSearchParams(current, next, 'analytics')
    const qs = patched.toString()
    router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
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

  const salesItems = salesQuery.data?.pages.flatMap((page) => page.items) ?? []
  const salesCurrency = salesQuery.data?.pages[0]?.currency ?? overviewQuery.data?.currency ?? 'usd'
  const hasMoreSales = salesQuery.data?.pages.at(-1)?.hasMore ?? false

  const overviewUpdating =
    overviewQuery.isFetching && (overviewQuery.isPlaceholderData || overviewQuery.isRefetching)
  const productsUpdating =
    productsQuery.isFetching && (productsQuery.isPlaceholderData || productsQuery.isRefetching)
  const salesUpdating = salesQuery.isFetching && !salesQuery.isFetchingNextPage

  if (pending) {
    return <SessionBlockSkeleton />
  }

  if (!authed) {
    return (
      <p className="text-sm text-muted-foreground">
        Sign in to view seller analytics for your storefront.
      </p>
    )
  }

  if (!verified) {
    return <EmailVerificationNotice />
  }

  const overviewLoading = overviewQuery.isPending && !overviewQuery.data
  const productsLoading = productsQuery.isPending && !productsQuery.data
  const salesLoading = salesQuery.isPending && !salesQuery.data

  const overview = overviewQuery.data
  const hasSales =
    overview != null &&
    (overview.grossRevenue.current > 0 || overview.orders.current > 0 || salesItems.length > 0)
  const hasProducts =
    (productsQuery.data?.totalCount ?? 0) > 0 ||
    (overview?.topAssets.length ?? 0) > 0 ||
    (overview?.topBundles.length ?? 0) > 0

  return (
    <div className="space-y-8">
      <div className="space-y-2">
        <p className="text-sm text-muted-foreground leading-relaxed">
          Commerce metrics for your AssetBlock sales. All dates and buckets use UTC; ranges use an
          exclusive end date on the API (<code className="text-xs">to</code> = day after the last
          included day).
        </p>
        {overview ? (
          <p className="text-xs text-muted-foreground">
            Data through {formatUtcDateTime(overview.generatedAt)} UTC
          </p>
        ) : null}
      </div>

      <AnalyticsRangePicker state={urlState} onChange={patchAnalyticsState} />

      {!hasSales && !overviewLoading && !overviewQuery.isError ? <AnalyticsNoSalesNotice /> : null}
      {!hasProducts && !productsLoading && !productsQuery.isError ? (
        <AnalyticsNoProductsNotice />
      ) : null}

      {overviewQuery.isError ? (
        <AnalyticsSectionError
          title="Overview unavailable"
          message={queryErrorMessage(overviewQuery.error, 'Could not load overview metrics.')}
          onRetry={() => overviewQuery.refetch()}
        />
      ) : overviewLoading ? (
        <>
          <AnalyticsKpiSkeletonGrid />
          <AnalyticsChartSkeleton />
        </>
      ) : overview ? (
        <>
          <AnalyticsKpiCards overview={overview} isUpdating={overviewUpdating} />
          <AnalyticsRevenueSplit overview={overview} isUpdating={overviewUpdating} />
          <AnalyticsSeriesChart
            series={overview.series}
            currency={overview.currency}
            granularity={overview.granularity}
            isUpdating={overviewUpdating}
          />
          <div className="grid gap-6 lg:grid-cols-2">
            <AnalyticsTopProducts
              title="Top assets"
              items={overview.topAssets}
              currency={overview.currency}
              emptyLabel="No asset sales in this range."
              isUpdating={overviewUpdating}
            />
            <AnalyticsTopProducts
              title="Top bundles"
              items={overview.topBundles}
              currency={overview.currency}
              emptyLabel="No bundle sales in this range."
              isUpdating={overviewUpdating}
            />
          </div>
        </>
      ) : null}

      {productsQuery.isError ? (
        <AnalyticsSectionError
          title="Products table unavailable"
          message={queryErrorMessage(productsQuery.error, 'Could not load product performance.')}
          onRetry={() => productsQuery.refetch()}
        />
      ) : productsLoading ? (
        <AnalyticsTableSkeleton />
      ) : productsQuery.data ? (
        <AnalyticsProductsTable
          data={productsQuery.data}
          state={urlState}
          onChange={patchAnalyticsState}
          isUpdating={productsUpdating}
          isPlaceholderData={productsQuery.isPlaceholderData}
        />
      ) : null}

      {salesQuery.isError && !salesQuery.data ? (
        <AnalyticsSectionError
          title="Recent sales unavailable"
          message={queryErrorMessage(salesQuery.error, 'Could not load recent sales.')}
          onRetry={() => salesQuery.refetch()}
        />
      ) : salesLoading ? (
        <AnalyticsListSkeleton />
      ) : (
        <AnalyticsRecentSales
          items={salesItems}
          currency={salesCurrency}
          hasMore={hasMoreSales}
          isFetchingMore={salesQuery.isFetchingNextPage}
          isFetchNextPageError={salesQuery.isFetchNextPageError}
          fetchNextPageError={salesQuery.isFetchNextPageError ? salesQuery.error : undefined}
          onLoadMore={() => salesQuery.fetchNextPage()}
          onRetryLoadMore={() => salesQuery.fetchNextPage()}
          isUpdating={salesUpdating}
        />
      )}
    </div>
  )
}
