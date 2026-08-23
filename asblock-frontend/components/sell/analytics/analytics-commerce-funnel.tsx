'use client'

import { formatCount, formatRatePercent } from '@/lib/analytics/analytics-format'
import type { SellerAnalyticsOverview } from '@/lib/analytics/analytics-types'

interface AnalyticsCommerceFunnelProps {
  overview: SellerAnalyticsOverview
  isUpdating?: boolean
}

export function AnalyticsCommerceFunnel({
  overview,
  isUpdating = false,
}: AnalyticsCommerceFunnelProps) {
  const funnel = overview.commerceFunnel
  if (!funnel) return null

  return (
    <section aria-labelledby="analytics-commerce-funnel-heading" aria-busy={isUpdating}>
      <h3 id="analytics-commerce-funnel-heading" className="text-base font-semibold">
        Commerce funnel
      </h3>
      <p className="mt-1 text-sm text-muted-foreground">
        Billing-grade checkout lifecycle from Stripe-attached sessions through completed orders.
      </p>
      <dl className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">Checkout starts</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatCount(funnel.checkoutStarts)}
          </dd>
        </div>
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">Stripe sessions attached</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatCount(funnel.stripeSessionsAttached)}
          </dd>
        </div>
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">Completed orders</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatCount(funnel.completedOrders)}
          </dd>
        </div>
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">Checkout completion rate</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatRatePercent(funnel.checkoutCompletionRate)}
          </dd>
          {funnel.checkoutCompletionRate == null ? (
            <p className="mt-1 text-xs text-muted-foreground">
              Unavailable — insufficient denominator
            </p>
          ) : null}
        </div>
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">Terminal abandonment rate</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatRatePercent(funnel.terminalAbandonmentRate)}
          </dd>
          {funnel.terminalAbandonmentRate == null ? (
            <p className="mt-1 text-xs text-muted-foreground">
              Unavailable — insufficient denominator
            </p>
          ) : null}
        </div>
        <div className="rounded-lg border border-border/60 bg-card/40 p-3">
          <dt className="text-xs text-muted-foreground">Pending checkouts</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">
            {formatCount(funnel.pendingCheckouts)}
          </dd>
        </div>
      </dl>
    </section>
  )
}
