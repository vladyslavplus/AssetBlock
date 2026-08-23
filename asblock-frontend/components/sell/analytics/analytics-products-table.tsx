'use client'

import Link from 'next/link'
import { ChevronLeft, ChevronRight } from 'lucide-react'

import { AnalyticsAvailabilityBadge } from '@/components/sell/analytics/analytics-availability-badge'
import { Button } from '@/components/ui/button'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import {
  formatCount,
  formatMoneyCents,
  formatRating,
  formatUtcShortDate,
} from '@/lib/analytics/analytics-format'
import {
  buildAnalyticsProductDetailHref,
  maxAccessibleProductsPage,
} from '@/lib/analytics/analytics-range'
import type {
  AnalyticsProductItem,
  AnalyticsProductsResult,
  AnalyticsUrlState,
  AnalyticsUtcRange,
} from '@/lib/analytics/analytics-types'

interface AnalyticsProductsTableProps {
  data: AnalyticsProductsResult
  state: AnalyticsUrlState
  utcRange: AnalyticsUtcRange
  onChange: (next: Partial<AnalyticsUrlState>) => void
  isUpdating?: boolean
  isPlaceholderData?: boolean
}

function sortLabel(value: AnalyticsUrlState['sort']): string {
  switch (value) {
    case 'REVENUE':
      return 'Revenue'
    case 'ORDERS':
      return 'Orders'
    case 'UNITS':
      return 'Units'
    case 'RATING':
      return 'Rating'
    case 'RECENT':
      return 'Recent sale'
  }
}

function productDetails(item: AnalyticsProductItem, currency: string): string {
  if (item.productKind === 'ASSET') {
    const direct =
      item.directRevenueCents != null ? formatMoneyCents(item.directRevenueCents, currency) : '—'
    const bundle =
      item.bundleAllocatedRevenueCents != null
        ? formatMoneyCents(item.bundleAllocatedRevenueCents, currency)
        : '—'
    return `Direct ${direct} · Bundle ${bundle}`
  }

  const price =
    item.currentPriceCents != null ? formatMoneyCents(item.currentPriceCents, currency) : '—'
  const list = item.listPriceCents != null ? formatMoneyCents(item.listPriceCents, currency) : '—'
  const discount =
    item.discountPercent != null ? `${item.discountPercent.toFixed(0)}% off` : 'No discount'
  return `${price} (list ${list}) · ${discount}`
}

function handleProductTypeChange(
  value: AnalyticsUrlState['productType'],
  state: AnalyticsUrlState,
  onChange: (next: Partial<AnalyticsUrlState>) => void,
) {
  if (value === 'BUNDLE' && state.sort === 'RATING') {
    onChange({ productType: value, sort: 'REVENUE', direction: 'DESC', page: 1 })
    return
  }
  onChange({ productType: value, page: 1 })
}

export function AnalyticsProductsTable({
  data,
  state,
  utcRange,
  onChange,
  isUpdating = false,
  isPlaceholderData = false,
}: AnalyticsProductsTableProps) {
  const totalPagesRaw = data.totalCount === 0 ? 0 : Math.ceil(data.totalCount / data.pageSize)
  const maxAccessible = maxAccessibleProductsPage(data.pageSize)
  const totalPages = totalPagesRaw === 0 ? 0 : Math.min(totalPagesRaw, maxAccessible)
  const displayPage = isPlaceholderData ? null : data.page
  const rangeStart =
    data.totalCount === 0 || displayPage == null ? 0 : (displayPage - 1) * data.pageSize + 1
  const rangeEnd =
    data.totalCount === 0 || displayPage == null
      ? 0
      : Math.min(displayPage * data.pageSize, data.totalCount)
  const paginationDisabled = isUpdating || isPlaceholderData

  return (
    <section
      aria-labelledby="analytics-products-heading"
      className="space-y-4"
      aria-busy={isUpdating}
    >
      <div className="flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <h2 id="analytics-products-heading" className="text-lg font-semibold">
            Product performance
          </h2>
          <p className="text-sm text-muted-foreground">
            Server-side sort and pagination · {formatCount(data.totalCount)} products
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Select
            value={state.productType}
            onValueChange={(value) =>
              handleProductTypeChange(value as AnalyticsUrlState['productType'], state, onChange)
            }
          >
            <SelectTrigger className="w-[140px]" aria-label="Filter product type">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="ALL">All products</SelectItem>
              <SelectItem value="ASSET">Assets</SelectItem>
              <SelectItem value="BUNDLE">Bundles</SelectItem>
            </SelectContent>
          </Select>
          <Select
            value={state.sort}
            onValueChange={(value) =>
              onChange({ sort: value as AnalyticsUrlState['sort'], page: 1 })
            }
          >
            <SelectTrigger className="w-[140px]" aria-label="Sort products">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="REVENUE">Revenue</SelectItem>
              <SelectItem value="ORDERS">Orders</SelectItem>
              <SelectItem value="UNITS">Units</SelectItem>
              {state.productType !== 'BUNDLE' ? (
                <SelectItem value="RATING">Rating</SelectItem>
              ) : null}
              <SelectItem value="RECENT">Recent sale</SelectItem>
            </SelectContent>
          </Select>
          <Select
            value={state.direction}
            onValueChange={(value) =>
              onChange({ direction: value as AnalyticsUrlState['direction'], page: 1 })
            }
          >
            <SelectTrigger className="w-[120px]" aria-label="Sort direction">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="DESC">Descending</SelectItem>
              <SelectItem value="ASC">Ascending</SelectItem>
            </SelectContent>
          </Select>
        </div>
      </div>

      <div className="overflow-x-auto rounded-lg border border-border/60">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">Product</TableHead>
              <TableHead scope="col">Type</TableHead>
              <TableHead scope="col">Status</TableHead>
              <TableHead scope="col">Details</TableHead>
              <TableHead scope="col" className="text-right">
                Revenue
              </TableHead>
              <TableHead scope="col" className="text-right">
                Orders
              </TableHead>
              <TableHead scope="col" className="text-right">
                Units
              </TableHead>
              <TableHead scope="col" className="text-right">
                Rating
              </TableHead>
              <TableHead scope="col" className="text-right">
                Latest sale
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {data.items.length === 0 ? (
              <TableRow>
                <TableCell colSpan={9} className="py-8 text-center text-muted-foreground">
                  No products match this filter in the selected range.
                </TableCell>
              </TableRow>
            ) : (
              data.items.map((item) => (
                <TableRow key={`${item.productKind}-${item.productId}`}>
                  <TableCell className="max-w-[16rem] truncate font-medium">
                    <Link
                      href={buildAnalyticsProductDetailHref(
                        item.productKind,
                        item.productId,
                        state,
                        utcRange,
                      )}
                      className="hover:text-primary hover:underline"
                    >
                      {item.title}
                    </Link>
                  </TableCell>
                  <TableCell>{item.productKind === 'ASSET' ? 'Asset' : 'Bundle'}</TableCell>
                  <TableCell>
                    <AnalyticsAvailabilityBadge availability={item.availability} />
                  </TableCell>
                  <TableCell className="max-w-[14rem] text-sm text-muted-foreground">
                    {productDetails(item, data.currency)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatMoneyCents(item.grossRevenueCents, data.currency)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatCount(item.orders)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatCount(item.unitsSold)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {item.averageRating != null ? formatRating(item.averageRating) : '—'}
                  </TableCell>
                  <TableCell className="text-right">
                    {item.latestSaleAt ? formatUtcShortDate(item.latestSaleAt) : '—'}
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </div>

      {totalPages > 1 ? (
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <p className="text-sm text-muted-foreground">
            {displayPage != null ? (
              <>
                Showing {formatCount(rangeStart)}–{formatCount(rangeEnd)} of{' '}
                {formatCount(data.totalCount)} · sorted by {sortLabel(state.sort)}{' '}
                {state.direction === 'DESC' ? '↓' : '↑'}
              </>
            ) : (
              <>Loading page…</>
            )}
          </p>
          <div className="flex items-center gap-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={paginationDisabled || state.page <= 1}
              onClick={() => onChange({ page: state.page - 1 })}
              aria-label="Previous page"
            >
              <ChevronLeft className="size-4" aria-hidden />
              Previous
            </Button>
            <span className="text-sm tabular-nums">
              {displayPage != null ? (
                <>
                  Page {displayPage} of {totalPages}
                </>
              ) : (
                <>Loading page…</>
              )}
            </span>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={paginationDisabled || state.page >= totalPages}
              onClick={() => onChange({ page: state.page + 1 })}
              aria-label="Next page"
            >
              Next
              <ChevronRight className="size-4" aria-hidden />
            </Button>
          </div>
        </div>
      ) : null}
    </section>
  )
}
