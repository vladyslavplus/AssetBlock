'use client'

import { formatCount, formatRatePercent } from '@/lib/analytics/analytics-format'
import type { SellerAnalyticsOverview } from '@/lib/analytics/analytics-types'

interface AnalyticsTrackedFunnelProps {
  overview: SellerAnalyticsOverview
  isUpdating?: boolean
}

export function AnalyticsTrackedFunnel({
  overview,
  isUpdating = false,
}: AnalyticsTrackedFunnelProps) {
  const funnel = overview.trackedFunnel
  if (!funnel) return null

  const coverage = overview.trackedCheckoutCoverage

  return (
    <section aria-labelledby="analytics-tracked-funnel-heading" aria-busy={isUpdating}>
      <h3 id="analytics-tracked-funnel-heading" className="text-base font-semibold">
        Tracked funnel
      </h3>
      <p className="mt-1 text-sm text-muted-foreground">
        Session-based telemetry funnel — separate from billing-grade commerce metrics above.
      </p>
      {coverage != null ? (
        <p className="mt-2 text-xs text-muted-foreground">
          Tracked checkout coverage: {formatRatePercent(coverage)} of checkout starts include
          attribution telemetry.
        </p>
      ) : (
        <p className="mt-2 text-xs text-muted-foreground">
          Tracked checkout coverage is unavailable for this range.
        </p>
      )}
      <dl className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">View sessions</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatCount(funnel.viewSessions)}
          </dd>
        </div>
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">Checkout sessions</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatCount(funnel.checkoutSessions)}
          </dd>
        </div>
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">Completed sessions</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatCount(funnel.completedSessions)}
          </dd>
        </div>
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">View → checkout rate</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatRatePercent(funnel.viewToCheckoutRate)}
          </dd>
        </div>
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">Checkout → completed rate</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatRatePercent(funnel.checkoutToCompletedRate)}
          </dd>
        </div>
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">View → completed rate</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatRatePercent(funnel.viewToCompletedRate)}
          </dd>
        </div>
      </dl>
    </section>
  )
}
