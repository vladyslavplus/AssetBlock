'use client'

import { Loader2 } from 'lucide-react'

import { AnalyticsSectionError } from '@/components/sell/analytics/analytics-section-error'
import { Button } from '@/components/ui/button'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { formatCount, formatMoneyCents, formatUtcDateTime } from '@/lib/analytics/analytics-format'
import type { AnalyticsSaleItem } from '@/lib/analytics/analytics-types'
import { cn } from '@/lib/utils'

interface AnalyticsRecentSalesProps {
  items: AnalyticsSaleItem[]
  currency: string
  hasMore: boolean
  isFetchingMore: boolean
  isFetchNextPageError?: boolean
  fetchNextPageError?: unknown
  onLoadMore: () => void
  onRetryLoadMore?: () => void
  isUpdating?: boolean
}

export function AnalyticsRecentSales({
  items,
  currency,
  hasMore,
  isFetchingMore,
  isFetchNextPageError = false,
  fetchNextPageError,
  onLoadMore,
  onRetryLoadMore,
  isUpdating = false,
}: AnalyticsRecentSalesProps) {
  const loadMoreErrorMessage =
    fetchNextPageError instanceof Error ? fetchNextPageError.message : 'Could not load more sales.'

  return (
    <section
      aria-labelledby="analytics-sales-heading"
      className={cn('space-y-4', isUpdating && 'opacity-80')}
      aria-busy={isUpdating}
    >
      <div>
        <h2 id="analytics-sales-heading" className="text-lg font-semibold">
          Recent sales
        </h2>
        <p className="text-sm text-muted-foreground">
          Newest orders first · buyer and payment details are not shown
          {isUpdating ? ' · Updating…' : ''}
        </p>
      </div>

      <div className="overflow-x-auto rounded-lg border border-border/60">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">Product</TableHead>
              <TableHead scope="col">Type</TableHead>
              <TableHead scope="col">Purchased (UTC)</TableHead>
              <TableHead scope="col" className="text-right">
                Units
              </TableHead>
              <TableHead scope="col" className="text-right">
                Revenue
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {items.length === 0 ? (
              <TableRow>
                <TableCell colSpan={5} className="py-8 text-center text-muted-foreground">
                  No sales in this range.
                </TableCell>
              </TableRow>
            ) : (
              items.map((sale) => (
                <TableRow key={`${sale.orderId}-${sale.productId}`}>
                  <TableCell className="max-w-[16rem] truncate font-medium">
                    {sale.productTitle}
                  </TableCell>
                  <TableCell>{sale.productKind === 'ASSET' ? 'Asset' : 'Bundle'}</TableCell>
                  <TableCell>{formatUtcDateTime(sale.purchasedAt)}</TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatCount(sale.units)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatMoneyCents(sale.grossRevenueCents, currency)}
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </div>

      {hasMore ? (
        <div className="flex flex-col items-center gap-3">
          {isFetchNextPageError ? (
            <AnalyticsSectionError
              title="Could not load more"
              message={loadMoreErrorMessage}
              onRetry={onRetryLoadMore ?? onLoadMore}
            />
          ) : (
            <Button
              type="button"
              variant="outline"
              onClick={onLoadMore}
              disabled={isFetchingMore}
              aria-busy={isFetchingMore}
            >
              {isFetchingMore ? (
                <>
                  <Loader2 className="mr-2 size-4 animate-spin" aria-hidden />
                  Loading…
                </>
              ) : (
                'Load more'
              )}
            </Button>
          )}
        </div>
      ) : null}
    </section>
  )
}
