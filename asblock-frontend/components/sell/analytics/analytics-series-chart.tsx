'use client'

import { useState } from 'react'
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from 'recharts'

import {
  ChartContainer,
  ChartTooltip,
  ChartTooltipContent,
  type ChartConfig,
} from '@/components/ui/chart'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import {
  formatCompactMoneyCents,
  formatCount,
  formatMoneyCents,
  formatUtcShortDate,
} from '@/lib/analytics/analytics-format'
import type {
  AnalyticsGranularity,
  AnalyticsSeriesMetric,
  AnalyticsSeriesPoint,
} from '@/lib/analytics/analytics-types'
import { cn } from '@/lib/utils'

interface AnalyticsSeriesChartProps {
  series: AnalyticsSeriesPoint[]
  currency: string
  granularity: AnalyticsGranularity
  isUpdating?: boolean
}

const chartConfig = {
  revenue: {
    label: 'Revenue',
    color: 'var(--chart-1)',
  },
  orders: {
    label: 'Orders',
    color: 'var(--chart-2)',
  },
  units: {
    label: 'Units sold',
    color: 'var(--chart-3)',
  },
} satisfies ChartConfig

function metricValue(point: AnalyticsSeriesPoint, metric: AnalyticsSeriesMetric): number {
  switch (metric) {
    case 'revenue':
      return point.grossRevenueCents
    case 'orders':
      return point.orders
    case 'units':
      return point.unitsSold
  }
}

function formatMetricValue(metric: AnalyticsSeriesMetric, value: number, currency: string): string {
  if (metric === 'revenue') return formatMoneyCents(value, currency)
  return formatCount(value)
}

function metricLabel(metric: AnalyticsSeriesMetric): string {
  switch (metric) {
    case 'revenue':
      return 'Gross revenue'
    case 'orders':
      return 'Orders'
    case 'units':
      return 'Units sold'
  }
}

function bucketLabel(bucketStart: string, granularity: AnalyticsGranularity): string {
  const start = formatUtcShortDate(bucketStart)
  switch (granularity) {
    case 'DAY':
      return start
    case 'WEEK':
      return `${start} (week start)`
    case 'MONTH':
      return `${start} (month start)`
  }
}

function granularityCaption(granularity: AnalyticsGranularity): string {
  switch (granularity) {
    case 'DAY':
      return 'Daily buckets'
    case 'WEEK':
      return 'Weekly buckets (week start shown)'
    case 'MONTH':
      return 'Monthly buckets (month start shown)'
  }
}

export function AnalyticsSeriesChart({
  series,
  currency,
  granularity,
  isUpdating = false,
}: AnalyticsSeriesChartProps) {
  const [metric, setMetric] = useState<AnalyticsSeriesMetric>('revenue')

  const chartData = series.map((point) => ({
    bucketStart: point.bucketStart,
    label: bucketLabel(point.bucketStart, granularity),
    value: metricValue(point, metric),
  }))

  const total = chartData.reduce((sum, point) => sum + point.value, 0)

  return (
    <section
      aria-labelledby="analytics-series-heading"
      className={cn('space-y-4', isUpdating && 'opacity-80')}
      aria-busy={isUpdating}
    >
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 id="analytics-series-heading" className="text-lg font-semibold">
            Performance over time
          </h2>
          <p className="text-sm text-muted-foreground">{granularityCaption(granularity)}</p>
        </div>
        <ToggleGroup
          type="single"
          value={metric}
          onValueChange={(value) => {
            if (value) setMetric(value as AnalyticsSeriesMetric)
          }}
          aria-label="Chart metric"
        >
          <ToggleGroupItem value="revenue" aria-label="Show revenue">
            Revenue
          </ToggleGroupItem>
          <ToggleGroupItem value="orders" aria-label="Show orders">
            Orders
          </ToggleGroupItem>
          <ToggleGroupItem value="units" aria-label="Show units sold">
            Units
          </ToggleGroupItem>
        </ToggleGroup>
      </div>

      {isUpdating ? <p className="text-xs text-muted-foreground">Updating…</p> : null}

      <p className="text-sm text-muted-foreground">
        Total {metricLabel(metric).toLowerCase()} in range:{' '}
        <span className="font-medium text-foreground tabular-nums">
          {formatMetricValue(metric, total, currency)}
        </span>{' '}
        across {formatCount(series.length)} buckets.
      </p>

      <ChartContainer config={chartConfig} className="min-h-[20rem] w-full aspect-auto">
        <BarChart data={chartData} accessibilityLayer margin={{ left: 8, right: 8, top: 8 }}>
          <CartesianGrid vertical={false} />
          <XAxis
            dataKey="label"
            tickLine={false}
            axisLine={false}
            tickMargin={8}
            minTickGap={24}
            interval="preserveStartEnd"
          />
          <YAxis
            tickLine={false}
            axisLine={false}
            tickFormatter={(value: number) =>
              metric === 'revenue' ? formatCompactMoneyCents(value, currency) : formatCount(value)
            }
            width={72}
          />
          <ChartTooltip
            content={(tooltipProps) => (
              <ChartTooltipContent
                {...tooltipProps}
                labelFormatter={(_, payload) =>
                  payload?.[0]?.payload?.label ? String(payload[0].payload.label) : ''
                }
                formatter={(value) => formatMetricValue(metric, Number(value), currency)}
              />
            )}
          />
          <Bar
            dataKey="value"
            fill={`var(--color-${metric})`}
            radius={4}
            isAnimationActive={false}
            name={metricLabel(metric)}
          />
        </BarChart>
      </ChartContainer>

      <div className="overflow-x-auto rounded-lg border border-border/60">
        <Table>
          <caption className="sr-only">
            Time series table fallback for {metricLabel(metric)}
          </caption>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">Bucket (UTC)</TableHead>
              <TableHead scope="col" className="text-right">
                {metricLabel(metric)}
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {chartData.map((row) => (
              <TableRow key={row.bucketStart}>
                <TableCell>{row.label}</TableCell>
                <TableCell className="text-right tabular-nums">
                  {formatMetricValue(metric, row.value, currency)}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </section>
  )
}
