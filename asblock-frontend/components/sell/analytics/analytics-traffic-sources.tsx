'use client'

import { useState } from 'react'
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from 'recharts'

import {
  ChartContainer,
  ChartTooltip,
  ChartTooltipContent,
  type ChartConfig,
} from '@/components/ui/chart'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { trafficSourceLabel } from '@/lib/analytics/analytics-engagement'
import { formatCount, formatMoneyCents } from '@/lib/analytics/analytics-format'
import type {
  AnalyticsTrafficSource,
  SellerAnalyticsOverview,
} from '@/lib/analytics/analytics-types'
import { cn } from '@/lib/utils'

interface AnalyticsTrafficSourcesProps {
  overview: SellerAnalyticsOverview
  engagementAvailable: boolean
  isUpdating?: boolean
}

type TrafficMetric = 'productViews' | 'uniqueVisitors' | 'attributedGrossRevenueCents'

const chartConfig = {
  productViews: { label: 'Product views', color: 'var(--chart-1)' },
  uniqueVisitors: { label: 'Unique visitors', color: 'var(--chart-2)' },
  attributedGrossRevenueCents: { label: 'Attributed revenue', color: 'var(--chart-3)' },
} satisfies ChartConfig

function metricLabel(metric: TrafficMetric): string {
  switch (metric) {
    case 'productViews':
      return 'Product views'
    case 'uniqueVisitors':
      return 'Unique visitors'
    case 'attributedGrossRevenueCents':
      return 'Attributed revenue'
  }
}

function formatMetric(metric: TrafficMetric, value: number, currency: string): string {
  if (metric === 'attributedGrossRevenueCents') return formatMoneyCents(value, currency)
  return formatCount(value)
}

function formatEngagementCount(value: number, engagementAvailable: boolean): string {
  return engagementAvailable ? formatCount(value) : 'Unavailable'
}

export function AnalyticsTrafficSources({
  overview,
  engagementAvailable,
  isUpdating = false,
}: AnalyticsTrafficSourcesProps) {
  const rows = overview.trafficSources
  const [metric, setMetric] = useState<TrafficMetric>(
    engagementAvailable ? 'productViews' : 'attributedGrossRevenueCents',
  )

  if (!rows || rows.length === 0) {
    return (
      <section aria-labelledby="analytics-traffic-heading" className="space-y-2">
        <h3 id="analytics-traffic-heading" className="text-base font-semibold">
          Traffic sources
        </h3>
        <p className="text-sm text-muted-foreground">No traffic source data in this range.</p>
      </section>
    )
  }

  const chartMetric = engagementAvailable ? metric : 'attributedGrossRevenueCents'

  const chartData = rows.map((row) => ({
    source: row.source,
    label: trafficSourceLabel(row.source),
    value: row[chartMetric],
  }))

  const externalRows = rows.filter((row) => row.source === 'EXTERNAL' && row.externalReferrers?.length)

  return (
    <section
      aria-labelledby="analytics-traffic-heading"
      className={cn('space-y-4', isUpdating && 'opacity-80')}
      aria-busy={isUpdating}
    >
      <div>
        <h3 id="analytics-traffic-heading" className="text-base font-semibold">
          Traffic sources
        </h3>
        <p className="text-sm text-muted-foreground">
          Checkout and order attribution is always available. Product views and visitors require
          engagement telemetry for the full selected range.
        </p>
      </div>

      <div className="flex flex-wrap gap-2">
        {(['productViews', 'uniqueVisitors', 'attributedGrossRevenueCents'] as const).map((key) => {
          const engagementMetric = key !== 'attributedGrossRevenueCents'
          const disabled = engagementMetric && !engagementAvailable
          const pressed = chartMetric === key

          return (
            <button
              key={key}
              type="button"
              className={cn(
                'rounded-md border px-3 py-1.5 text-xs font-medium transition-colors',
                pressed
                  ? 'border-primary/50 bg-primary/10 text-foreground'
                  : 'border-border/60 text-muted-foreground hover:text-foreground',
                disabled && 'cursor-not-allowed opacity-50',
              )}
              onClick={() => {
                if (!disabled) setMetric(key)
              }}
              disabled={disabled}
              aria-pressed={pressed}
            >
              {metricLabel(key)}
            </button>
          )
        })}
      </div>

      <ChartContainer config={chartConfig} className="min-h-[16rem] w-full aspect-auto">
        <BarChart data={chartData} accessibilityLayer margin={{ left: 8, right: 8, top: 8 }}>
          <CartesianGrid vertical={false} />
          <XAxis
            dataKey="label"
            tickLine={false}
            axisLine={false}
            tickMargin={8}
            interval={0}
            angle={-20}
            textAnchor="end"
            height={72}
          />
          <YAxis
            tickLine={false}
            axisLine={false}
            tickFormatter={(value: number) =>
              chartMetric === 'attributedGrossRevenueCents'
                ? formatMoneyCents(value, overview.currency)
                : formatCount(value)
            }
            width={80}
          />
          <ChartTooltip
            content={(tooltipProps) => (
              <ChartTooltipContent
                {...tooltipProps}
                formatter={(value) => formatMetric(chartMetric, Number(value), overview.currency)}
              />
            )}
          />
          <Bar
            dataKey="value"
            fill={`var(--color-${chartMetric})`}
            radius={4}
            isAnimationActive={false}
            name={metricLabel(chartMetric)}
          />
        </BarChart>
      </ChartContainer>

      <div className="overflow-x-auto rounded-lg border border-border/60">
        <Table>
          <caption className="sr-only">Traffic sources table</caption>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">Source</TableHead>
              <TableHead scope="col" className="text-right">
                Views
              </TableHead>
              <TableHead scope="col" className="text-right">
                Visitors
              </TableHead>
              <TableHead scope="col" className="text-right">
                Checkouts
              </TableHead>
              <TableHead scope="col" className="text-right">
                Orders
              </TableHead>
              <TableHead scope="col" className="text-right">
                Revenue
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((row) => (
              <TableRow key={row.source}>
                <TableCell>{trafficSourceLabel(row.source as AnalyticsTrafficSource)}</TableCell>
                <TableCell className="text-right tabular-nums">
                  {formatEngagementCount(row.productViews, engagementAvailable)}
                </TableCell>
                <TableCell className="text-right tabular-nums">
                  {formatEngagementCount(row.uniqueVisitors, engagementAvailable)}
                </TableCell>
                <TableCell className="text-right tabular-nums">
                  {formatCount(row.checkoutStarts)}
                </TableCell>
                <TableCell className="text-right tabular-nums">
                  {formatCount(row.completedOrders)}
                </TableCell>
                <TableCell className="text-right tabular-nums">
                  {formatMoneyCents(row.attributedGrossRevenueCents, overview.currency)}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>

      {externalRows.length > 0 ? (
        <div className="space-y-3">
          <h4 className="text-sm font-medium">External referrer hosts</h4>
          <div className="overflow-x-auto rounded-lg border border-border/60">
            <Table>
              <caption className="sr-only">External referrer host breakdown</caption>
              <TableHeader>
                <TableRow>
                  <TableHead scope="col">Host</TableHead>
                  <TableHead scope="col" className="text-right">
                    Views
                  </TableHead>
                  <TableHead scope="col" className="text-right">
                    Visitors
                  </TableHead>
                  <TableHead scope="col" className="text-right">
                    Checkouts
                  </TableHead>
                  <TableHead scope="col" className="text-right">
                    Orders
                  </TableHead>
                  <TableHead scope="col" className="text-right">
                    Revenue
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {externalRows.flatMap((row) =>
                  (row.externalReferrers ?? []).map((referrer) => (
                    <TableRow key={referrer.referrerHost}>
                      <TableCell className="font-mono text-sm">{referrer.referrerHost}</TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatEngagementCount(referrer.productViews, engagementAvailable)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatEngagementCount(referrer.uniqueVisitors, engagementAvailable)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatCount(referrer.checkoutStarts)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatCount(referrer.completedOrders)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatMoneyCents(referrer.attributedGrossRevenueCents, overview.currency)}
                      </TableCell>
                    </TableRow>
                  )),
                )}
              </TableBody>
            </Table>
          </div>
        </div>
      ) : (
        <p className="text-sm text-muted-foreground">No external referrer hosts in this range.</p>
      )}
    </section>
  )
}
