import { formatMoneyCents } from '@/lib/analytics/analytics-format'
import type { SellerAnalyticsOverview } from '@/lib/analytics/analytics-types'

interface AnalyticsRevenueSplitProps {
  overview: SellerAnalyticsOverview
  isUpdating?: boolean
}

export function AnalyticsRevenueSplit({
  overview,
  isUpdating = false,
}: AnalyticsRevenueSplitProps) {
  const direct = overview.directRevenue.current
  const bundle = overview.bundleRevenue.current
  const gross = overview.grossRevenue.current
  const directPct = gross > 0 ? (direct / gross) * 100 : 0
  const bundlePct = gross > 0 ? (bundle / gross) * 100 : 0

  return (
    <section
      aria-labelledby="analytics-revenue-split-heading"
      className="space-y-4"
      aria-busy={isUpdating}
    >
      <h2 id="analytics-revenue-split-heading" className="text-lg font-semibold">
        Direct vs bundle revenue
      </h2>
      <div className="grid gap-4 sm:grid-cols-2">
        <article className="rounded-lg border border-border/60 bg-card/40 p-4">
          <p className="text-sm text-muted-foreground">Direct asset sales</p>
          <p className="mt-2 text-2xl font-semibold tabular-nums">
            {formatMoneyCents(direct, overview.currency)}
          </p>
          <p className="mt-1 text-xs text-muted-foreground">{directPct.toFixed(1)}% of gross</p>
        </article>
        <article className="rounded-lg border border-border/60 bg-card/40 p-4">
          <p className="text-sm text-muted-foreground">Bundle sales</p>
          <p className="mt-2 text-2xl font-semibold tabular-nums">
            {formatMoneyCents(bundle, overview.currency)}
          </p>
          <p className="mt-1 text-xs text-muted-foreground">{bundlePct.toFixed(1)}% of gross</p>
        </article>
      </div>
      <div
        className="flex h-3 overflow-hidden rounded-full bg-muted"
        role="img"
        aria-label={`Direct ${directPct.toFixed(1)} percent, bundle ${bundlePct.toFixed(1)} percent`}
      >
        <div
          className="bg-primary"
          style={{ width: `${directPct}%` }}
          title={`Direct ${directPct.toFixed(1)}%`}
        />
        <div
          className="bg-violet-400"
          style={{ width: `${bundlePct}%` }}
          title={`Bundle ${bundlePct.toFixed(1)}%`}
        />
      </div>
      <p className="text-xs text-muted-foreground">
        <span className="inline-flex items-center gap-1">
          <span className="inline-block size-2 rounded-full bg-primary" aria-hidden />
          Direct
        </span>
        <span className="mx-3 inline-flex items-center gap-1">
          <span className="inline-block size-2 rounded-full bg-violet-400" aria-hidden />
          Bundle
        </span>
      </p>
    </section>
  )
}
