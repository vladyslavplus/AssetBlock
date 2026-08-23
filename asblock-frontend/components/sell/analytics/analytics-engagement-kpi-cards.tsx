'use client'

import { TrendingDown, TrendingUp } from 'lucide-react'

import {
  engagementComparisonLabel,
  type EngagementCountMetricLike,
} from '@/lib/analytics/analytics-engagement'
import { formatCount, formatPercentageChange } from '@/lib/analytics/analytics-format'
import type { SellerAnalyticsOverview } from '@/lib/analytics/analytics-types'
import { cn } from '@/lib/utils'

interface AnalyticsEngagementKpiCardsProps {
  overview: SellerAnalyticsOverview
  isUpdating?: boolean
}

interface EngagementCard {
  key: string
  label: string
  description: string
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

function engagementCard(
  key: string,
  label: string,
  description: string,
  metric: EngagementCountMetricLike,
): EngagementCard {
  const deltaText = engagementComparisonLabel(metric)
  const pctDelta = formatPercentageChange(metric.percentageChange)
  return {
    key,
    label,
    description,
    value: formatCount(metric.current),
    delta: pctDelta ? `${pctDelta} vs prior period` : deltaText,
    unavailableMessage: pctDelta || deltaText ? null : 'Comparison unavailable',
    trend: metricTrend(pctDelta),
  }
}

export function AnalyticsEngagementKpiCards({
  overview,
  isUpdating = false,
}: AnalyticsEngagementKpiCardsProps) {
  const totals = overview.engagementTotals
  if (!totals) return null

  const cards: EngagementCard[] = [
    engagementCard(
      'views',
      'Product views',
      'Page views on product detail pages',
      totals.productViews,
    ),
    engagementCard(
      'visitors',
      'Unique visitors',
      'Distinct visitor sessions in this range',
      totals.uniqueVisitors,
    ),
    engagementCard(
      'downloads',
      'Download requests',
      'Authorized download request/start events — not completed transfers',
      totals.downloadRequests,
    ),
  ]

  return (
    <div className="space-y-2" aria-busy={isUpdating} aria-live="polite">
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
        {cards.map((card) => (
          <article
            key={card.key}
            aria-label={`${card.label}: ${card.value}`}
            className="rounded-lg border border-border/60 bg-card/40 p-4 min-h-[7.5rem]"
          >
            <p className="text-sm text-muted-foreground">{card.label}</p>
            <p className="mt-1 text-xs text-muted-foreground/80">{card.description}</p>
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
                <span>{card.delta}</span>
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
