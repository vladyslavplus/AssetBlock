import Link from 'next/link'
import { Package, Sparkles } from 'lucide-react'

import { AnalyticsAvailabilityBadge } from '@/components/sell/analytics/analytics-availability-badge'
import {
  formatCount,
  formatMoneyCents,
  formatRating,
  formatUtcShortDate,
} from '@/lib/analytics/analytics-format'
import type { AnalyticsProductItem } from '@/lib/analytics/analytics-types'
import { cn } from '@/lib/utils'

interface AnalyticsTopProductsProps {
  title: string
  items: AnalyticsProductItem[]
  currency: string
  emptyLabel: string
  isUpdating?: boolean
}

function TopProductRow({ item, currency }: { item: AnalyticsProductItem; currency: string }) {
  const Icon = item.productKind === 'BUNDLE' ? Package : Sparkles

  return (
    <li className="flex items-start gap-3 rounded-md border border-border/50 px-3 py-3">
      <Icon className="mt-0.5 size-4 shrink-0 text-primary" aria-hidden />
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <p className="font-medium truncate">{item.title}</p>
          <AnalyticsAvailabilityBadge availability={item.availability} />
        </div>
        <p className="mt-1 text-sm text-muted-foreground tabular-nums">
          {formatMoneyCents(item.grossRevenueCents, currency)} · {formatCount(item.orders)} orders ·{' '}
          {formatCount(item.unitsSold)} units
        </p>
        {item.averageRating != null ? (
          <p className="mt-1 text-xs text-muted-foreground">
            Rating {formatRating(item.averageRating)}
            {item.reviewCount != null ? ` (${formatCount(item.reviewCount)} reviews)` : ''}
          </p>
        ) : null}
        {item.latestSaleAt ? (
          <p className="mt-1 text-xs text-muted-foreground">
            Latest sale {formatUtcShortDate(item.latestSaleAt)}
          </p>
        ) : null}
      </div>
    </li>
  )
}

export function AnalyticsTopProducts({
  title,
  items,
  currency,
  emptyLabel,
  isUpdating = false,
}: AnalyticsTopProductsProps) {
  return (
    <section
      aria-labelledby={`top-${title.replace(/\s+/g, '-').toLowerCase()}`}
      className={cn('space-y-3', isUpdating && 'opacity-80')}
      aria-busy={isUpdating}
    >
      <div className="flex items-center justify-between gap-2">
        <h3
          id={`top-${title.replace(/\s+/g, '-').toLowerCase()}`}
          className="text-base font-semibold"
        >
          {title}
        </h3>
        {isUpdating ? <span className="text-xs text-muted-foreground">Updating…</span> : null}
      </div>
      {items.length === 0 ? (
        <p className="text-sm text-muted-foreground">{emptyLabel}</p>
      ) : (
        <ul className="space-y-2">
          {items.map((item) => (
            <TopProductRow key={item.productId} item={item} currency={currency} />
          ))}
        </ul>
      )}
    </section>
  )
}

export function AnalyticsNoProductsNotice() {
  return (
    <div className="rounded-lg border border-border/60 bg-card/40 px-4 py-4 text-sm text-muted-foreground">
      No product performance yet.{' '}
      <Link
        href="/sell?tab=upload"
        className="font-medium text-primary underline-offset-4 hover:underline"
      >
        Upload an asset
      </Link>{' '}
      to start selling.
    </div>
  )
}

export function AnalyticsNoSalesNotice() {
  return (
    <div className="rounded-lg border border-border/60 bg-card/40 px-4 py-4 text-sm text-muted-foreground">
      No completed sales in this range yet. Revenue appears after Stripe checkout webhooks confirm
      payment — it can take a minute after a successful purchase.
    </div>
  )
}
