'use client'

import Link from 'next/link'

import { AnalyticsAvailabilityBadge } from '@/components/sell/analytics/analytics-availability-badge'
import { AnalyticsSeriesChart } from '@/components/sell/analytics/analytics-series-chart'
import {
  formatCount,
  formatMoneyCents,
  formatRatePercent,
  formatUtcDateTime,
  formatUtcShortDate,
} from '@/lib/analytics/analytics-format'
import { hasFullEngagementCoverage } from '@/lib/analytics/analytics-engagement'
import type { AnalyticsAssetDetail, AnalyticsBundleDetail } from '@/lib/analytics/analytics-types'

interface AnalyticsProductDetailMetricsProps {
  detail: AnalyticsAssetDetail | AnalyticsBundleDetail
  kind: 'ASSET' | 'BUNDLE'
}

export function AnalyticsProductDetailMetrics({
  detail,
  kind,
}: AnalyticsProductDetailMetricsProps) {
  const fullEngagementCoverage = hasFullEngagementCoverage(
    detail.engagementAvailableFrom,
    detail.from,
  )
  const assetDetail = kind === 'ASSET' ? (detail as AnalyticsAssetDetail) : null
  const bundleDetail = kind === 'BUNDLE' ? (detail as AnalyticsBundleDetail) : null

  return (
    <div className="space-y-8">
      <div className="space-y-1">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="text-2xl font-semibold">{detail.title}</h1>
          <AnalyticsAvailabilityBadge availability={detail.availability} />
        </div>
        <p className="text-sm text-muted-foreground">
          {kind === 'ASSET' ? 'Asset' : 'Bundle'} analytics · {detail.from} – {detail.to} UTC
          (exclusive end)
        </p>
        <p className="text-xs text-muted-foreground">
          Generated {formatUtcDateTime(detail.generatedAt)} UTC
        </p>
      </div>

      <section aria-labelledby="detail-commerce-heading" className="space-y-4">
        <h2 id="detail-commerce-heading" className="text-lg font-semibold">
          Commerce
        </h2>
        <dl className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <div className="rounded-lg border border-border/60 bg-card/40 p-3">
            <dt className="text-xs text-muted-foreground">Gross revenue</dt>
            <dd className="mt-1 text-lg font-semibold tabular-nums">
              {formatMoneyCents(detail.grossRevenueCents, detail.currency)}
            </dd>
          </div>
          <div className="rounded-lg border border-border/60 bg-card/40 p-3">
            <dt className="text-xs text-muted-foreground">Orders</dt>
            <dd className="mt-1 text-lg font-semibold tabular-nums">
              {formatCount(detail.orders)}
            </dd>
          </div>
          <div className="rounded-lg border border-border/60 bg-card/40 p-3">
            <dt className="text-xs text-muted-foreground">Units sold</dt>
            <dd className="mt-1 text-lg font-semibold tabular-nums">
              {formatCount(detail.unitsSold)}
            </dd>
          </div>
          <div className="rounded-lg border border-border/60 bg-card/40 p-3">
            <dt className="text-xs text-muted-foreground">Latest sale</dt>
            <dd className="mt-1 text-lg font-semibold">
              {detail.latestSaleAt ? formatUtcShortDate(detail.latestSaleAt) : '—'}
            </dd>
          </div>
          <div className="rounded-lg border border-border/60 bg-card/40 p-3">
            <dt className="text-xs text-muted-foreground">Checkout starts</dt>
            <dd className="mt-1 text-lg font-semibold tabular-nums">
              {formatCount(detail.checkoutStarts)}
            </dd>
          </div>
          <div className="rounded-lg border border-border/60 bg-card/40 p-3">
            <dt className="text-xs text-muted-foreground">Checkout completion rate</dt>
            <dd className="mt-1 text-lg font-semibold tabular-nums">
              {formatRatePercent(detail.checkoutCompletionRate)}
            </dd>
          </div>
        </dl>

        {assetDetail ? (
          <p className="text-sm text-muted-foreground">
            Direct {formatMoneyCents(assetDetail.directRevenueCents, detail.currency)} · Bundle
            allocated {formatMoneyCents(assetDetail.bundleAllocatedRevenueCents, detail.currency)}
          </p>
        ) : bundleDetail ? (
          <p className="text-sm text-muted-foreground">
            Price{' '}
            {bundleDetail.currentPriceCents != null
              ? formatMoneyCents(bundleDetail.currentPriceCents, detail.currency)
              : '—'}{' '}
            · List{' '}
            {bundleDetail.listPriceCents != null
              ? formatMoneyCents(bundleDetail.listPriceCents, detail.currency)
              : '—'}
          </p>
        ) : null}
      </section>

      <section aria-labelledby="detail-engagement-heading" className="space-y-4">
        <h2 id="detail-engagement-heading" className="text-lg font-semibold">
          Engagement
        </h2>
        {!fullEngagementCoverage ? (
          <p className="text-sm text-muted-foreground">
            Engagement metrics are unavailable for this range
            {detail.engagementAvailableFrom
              ? ` (telemetry from ${formatUtcDateTime(detail.engagementAvailableFrom)} UTC).`
              : '.'}
          </p>
        ) : (
          <dl className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <div className="rounded-lg border border-border/60 bg-card/40 p-3">
              <dt className="text-xs text-muted-foreground">Product views</dt>
              <dd className="mt-1 text-lg font-semibold tabular-nums">
                {detail.productViews != null ? formatCount(detail.productViews) : '—'}
              </dd>
            </div>
            <div className="rounded-lg border border-border/60 bg-card/40 p-3">
              <dt className="text-xs text-muted-foreground">Unique visitors</dt>
              <dd className="mt-1 text-lg font-semibold tabular-nums">
                {detail.uniqueVisitors != null ? formatCount(detail.uniqueVisitors) : '—'}
              </dd>
            </div>
            {assetDetail ? (
              <div className="rounded-lg border border-border/60 bg-card/40 p-3">
                <dt className="text-xs text-muted-foreground">Download requests</dt>
                <dd className="mt-1 text-lg font-semibold tabular-nums">
                  {assetDetail.downloadRequests != null
                    ? formatCount(assetDetail.downloadRequests)
                    : '—'}
                </dd>
                <p className="mt-1 text-xs text-muted-foreground">
                  Authorized request/start — not completed transfer
                </p>
              </div>
            ) : null}
            <div className="rounded-lg border border-border/60 bg-card/40 p-3">
              <dt className="text-xs text-muted-foreground">View → checkout rate</dt>
              <dd className="mt-1 text-lg font-semibold tabular-nums">
                {formatRatePercent(detail.trackedViewToCheckoutRate)}
              </dd>
            </div>
          </dl>
        )}
      </section>

      <AnalyticsSeriesChart
        series={detail.series}
        currency={detail.currency}
        granularity={detail.granularity}
      />
    </div>
  )
}

interface AnalyticsDetailBackLinkProps {
  href: string
}

export function AnalyticsDetailBackLink({ href }: AnalyticsDetailBackLinkProps) {
  return (
    <Link
      href={href}
      className="inline-flex items-center text-sm text-muted-foreground hover:text-foreground"
    >
      ← Back to analytics
    </Link>
  )
}
