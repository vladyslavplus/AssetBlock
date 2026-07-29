'use client'

import { TrendingDown, TrendingUp } from 'lucide-react'

import {
  formatCount,
  formatMoneyCents,
  formatPercentageChange,
  formatRatePercent,
} from '@/lib/analytics/analytics-format'
import type {
  CountMetric,
  MoneyCentsMetric,
  RateMetric,
  SellerAnalyticsOverview,
} from '@/lib/analytics/analytics-types'
import { cn } from '@/lib/utils'

interface AnalyticsKpiCardsProps {
  overview: SellerAnalyticsOverview
  isUpdating?: boolean
}

interface KpiCard {
  key: string
  label: string
  value: string
  delta: string | null
  unavailableMessage: string | null
  trend: 'up' | 'down' | 'neutral'
}

function metricTrend(delta: string | null): 'up' | 'down' | 'neutral' {
  if (!delta) return 'neutral'
  if (delta.startsWith('+')) return 'up'
  if (delta.startsWith('-')) return 'down'
  return 'neutral'
}

function unavailableComparisonMessage(current: number, previous: number): string {
  if (previous === 0 && current === 0) {
    return 'No prior-period baseline'
  }
  return 'Comparison unavailable (new baseline)'
}

function moneyCard(
  key: string,
  label: string,
  metric: MoneyCentsMetric,
  currency: string,
): KpiCard {
  const delta = formatPercentageChange(metric.percentageChange)
  return {
    key,
    label,
    value: formatMoneyCents(metric.current, currency),
    delta,
    unavailableMessage:
      delta == null ? unavailableComparisonMessage(metric.current, metric.previous) : null,
    trend: metricTrend(delta),
  }
}

function countCard(key: string, label: string, metric: CountMetric): KpiCard {
  const delta = formatPercentageChange(metric.percentageChange)
  return {
    key,
    label,
    value: formatCount(metric.current),
    delta,
    unavailableMessage:
      delta == null ? unavailableComparisonMessage(metric.current, metric.previous) : null,
    trend: metricTrend(delta),
  }
}

function rateCard(key: string, label: string, metric: RateMetric): KpiCard {
  const delta = formatPercentageChange(metric.percentageChange)
  let unavailableMessage: string | null = null
  if (metric.current == null) {
    unavailableMessage = 'Unavailable — no customers in this period'
  } else if (delta == null) {
    if (metric.previous === 0 && metric.current === 0) {
      unavailableMessage = 'No prior-period baseline'
    } else {
      unavailableMessage = 'Comparison unavailable (new baseline)'
    }
  }
  return {
    key,
    label,
    value: formatRatePercent(metric.current),
    delta: metric.current == null ? null : delta,
    unavailableMessage,
    trend: metric.current == null ? 'neutral' : metricTrend(delta),
  }
}

export function AnalyticsKpiCards({ overview, isUpdating = false }: AnalyticsKpiCardsProps) {
  const cards: KpiCard[] = [
    moneyCard('gross', 'Gross revenue', overview.grossRevenue, overview.currency),
    countCard('orders', 'Orders', overview.orders),
    countCard('units', 'Units sold', overview.unitsSold),
    countCard('customers', 'Unique customers', overview.uniqueCustomers),
    moneyCard('aov', 'Average order value', overview.averageOrderValue, overview.currency),
    rateCard('repeat', 'Repeat customer rate', overview.repeatCustomerRate),
  ]

  return (
    <div
      className={cn('space-y-2', isUpdating && 'opacity-80')}
      aria-busy={isUpdating}
      aria-live="polite"
    >
      {isUpdating ? <p className="text-xs text-muted-foreground">Updating…</p> : null}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
        {cards.map((card) => (
          <article
            key={card.key}
            aria-label={`${card.label}: ${card.value}`}
            className="rounded-lg border border-border/60 bg-card/40 p-4 min-h-[7.5rem]"
          >
            <p className="text-sm text-muted-foreground">{card.label}</p>
            <p className="mt-2 text-2xl font-semibold tabular-nums">{card.value}</p>
            {card.delta ? (
              <p
                className={cn(
                  'mt-2 flex items-center gap-1 text-xs font-medium',
                  card.trend === 'up' && 'text-emerald-400',
                  card.trend === 'down' && 'text-rose-400',
                  card.trend === 'neutral' && 'text-muted-foreground',
                )}
              >
                {card.trend === 'up' ? (
                  <TrendingUp className="size-3.5" aria-hidden />
                ) : card.trend === 'down' ? (
                  <TrendingDown className="size-3.5" aria-hidden />
                ) : null}
                <span>{card.delta} vs prior period</span>
              </p>
            ) : card.unavailableMessage ? (
              <p className="mt-2 text-xs text-muted-foreground">{card.unavailableMessage}</p>
            ) : null}
          </article>
        ))}
      </div>
    </div>
  )
}
