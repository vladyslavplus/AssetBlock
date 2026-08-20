'use client'

import { Info } from 'lucide-react'

import {
  hasAnyEngagementCoverage,
  includesCurrentUtcDay,
} from '@/lib/analytics/analytics-engagement'
import { formatUtcDateTime } from '@/lib/analytics/analytics-format'
import type { AnalyticsUtcRange, SellerAnalyticsOverview } from '@/lib/analytics/analytics-types'

interface AnalyticsEngagementNoticesProps {
  overview: SellerAnalyticsOverview
  utcRange: AnalyticsUtcRange
  hasSales: boolean
}

export function AnalyticsEngagementNotices({
  overview,
  utcRange,
  hasSales,
}: AnalyticsEngagementNoticesProps) {
  const hasAnyEngagement = hasAnyEngagementCoverage(overview.engagementAvailableFrom, utcRange.to)
  const currentDayIncluded = includesCurrentUtcDay(utcRange.from, utcRange.to)

  const notices: string[] = []

  if (overview.engagementAvailableFrom) {
    notices.push(
      `Engagement telemetry available from ${formatUtcDateTime(overview.engagementAvailableFrom)} UTC.`,
    )
  } else {
    notices.push('Engagement telemetry is not yet available for your storefront.')
  }

  if (!hasAnyEngagement) {
    if (hasSales) {
      notices.push(
        'Commerce metrics below are available. Engagement metrics are unavailable for this range — it may predate instrumentation.',
      )
    } else {
      notices.push('Engagement metrics are unavailable for this range.')
    }
  }

  if (currentDayIncluded) {
    notices.push(
      'The current UTC day is still in progress — today’s counts may change until the day closes.',
    )
  }

  return (
    <div
      role="note"
      className="rounded-lg border border-border/60 bg-muted/20 px-4 py-3 text-sm text-muted-foreground"
    >
      <div className="flex items-start gap-2">
        <Info className="mt-0.5 size-4 shrink-0" aria-hidden />
        <ul className="list-disc space-y-1 pl-4">
          {notices.map((notice) => (
            <li key={notice}>{notice}</li>
          ))}
        </ul>
      </div>
    </div>
  )
}

export function AnalyticsEngagementEmptyNotice({ hasSales }: { hasSales: boolean }) {
  if (hasSales) {
    return (
      <p className="text-sm text-muted-foreground">
        No engagement telemetry in this range. Commerce metrics above remain billing-grade and
        complete.
      </p>
    )
  }

  return (
    <p className="text-sm text-muted-foreground">
      No engagement telemetry in this range. Publish products and wait for visitor activity, or
      select a range after instrumentation began.
    </p>
  )
}
