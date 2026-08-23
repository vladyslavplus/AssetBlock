'use client'

import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { notFound, useSearchParams } from 'next/navigation'

import { useAuth } from '@/components/auth/auth-context'
import {
  EmailVerificationNotice,
  isEmailVerified,
} from '@/components/auth/email-verification-notice'
import {
  AnalyticsDetailBackLink,
  AnalyticsProductDetailMetrics,
} from '@/components/sell/analytics/analytics-product-detail-shared'
import { AnalyticsSectionError } from '@/components/sell/analytics/analytics-section-error'
import {
  AnalyticsChartSkeleton,
  AnalyticsKpiSkeletonGrid,
} from '@/components/sell/analytics/analytics-skeletons'
import {
  buildAnalyticsDashboardHref,
  canonicalizeAnalyticsState,
  parseAnalyticsSearchParams,
  resolveAnalyticsUtcRange,
} from '@/lib/analytics/analytics-range'
import { sellerAssetDetailQueryOptions } from '@/lib/analytics/analytics-query'
import { ApiRequestError } from '@/lib/http/api-client'
import { runQueryInBackground } from '@/lib/query/query-refresh'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'

interface AnalyticsAssetDetailViewProps {
  assetId: string
}

function queryErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof ApiRequestError) {
    if (error.status === 401) return 'Please sign in to view analytics.'
    if (error.status === 403) return 'Verify your email address to view seller analytics.'
    if (error.status === 404) return 'Asset not found.'
  }
  return fallback
}

export function AnalyticsAssetDetailView({ assetId }: AnalyticsAssetDetailViewProps) {
  const searchParams = useSearchParams()
  const { status, user } = useAuth()
  const authed = status === 'authenticated'
  const pending = status === 'loading'
  const verified = isEmailVerified(user)

  const urlState = canonicalizeAnalyticsState(parseAnalyticsSearchParams(searchParams))
  const utcRange = resolveAnalyticsUtcRange(urlState)
  const backHref = buildAnalyticsDashboardHref(urlState)

  const detailQuery = useQuery({
    ...sellerAssetDetailQueryOptions(assetId, utcRange),
    enabled: authed && verified,
    placeholderData: keepPreviousData,
  })

  if (pending) return <SessionBlockSkeleton />
  if (!authed) {
    return (
      <p className="text-sm text-muted-foreground">
        Sign in to view seller analytics for your storefront.
      </p>
    )
  }
  if (!verified) return <EmailVerificationNotice />

  if (detailQuery.isError) {
    if (detailQuery.error instanceof ApiRequestError && detailQuery.error.status === 404) {
      notFound()
    }
    return (
      <AnalyticsSectionError
        title="Asset analytics unavailable"
        message={queryErrorMessage(detailQuery.error, 'Could not load asset analytics.')}
        onRetry={() => runQueryInBackground(detailQuery.refetch())}
      />
    )
  }

  const loading = detailQuery.isPending && !detailQuery.data

  return (
    <div className="space-y-6">
      <AnalyticsDetailBackLink href={backHref} />
      {loading ? (
        <>
          <AnalyticsKpiSkeletonGrid />
          <AnalyticsChartSkeleton />
        </>
      ) : detailQuery.data ? (
        <AnalyticsProductDetailMetrics detail={detailQuery.data} kind="ASSET" />
      ) : null}
    </div>
  )
}
