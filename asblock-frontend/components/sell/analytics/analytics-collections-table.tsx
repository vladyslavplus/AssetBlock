'use client'

import { ChevronLeft, ChevronRight } from 'lucide-react'

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
import { collectionSortLabel, collectionStatusLabel } from '@/lib/analytics/analytics-engagement'
import { formatCount, formatMoneyCents, formatRatePercent } from '@/lib/analytics/analytics-format'
import { maxAccessibleProductsPage } from '@/lib/analytics/analytics-range'
import type { AnalyticsCollectionsResult, AnalyticsUrlState } from '@/lib/analytics/analytics-types'

interface AnalyticsCollectionsTableProps {
  data: AnalyticsCollectionsResult
  state: AnalyticsUrlState
  onChange: (next: Partial<AnalyticsUrlState>) => void
  isUpdating?: boolean
  isPlaceholderData?: boolean
}

function formatNullableCount(value: number | null | undefined): string {
  return value == null ? '—' : formatCount(value)
}

export function AnalyticsCollectionsTable({
  data,
  state,
  onChange,
  isUpdating = false,
  isPlaceholderData = false,
}: AnalyticsCollectionsTableProps) {
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
      aria-labelledby="analytics-collections-heading"
      className="space-y-4"
      aria-busy={isUpdating}
    >
      <div className="flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <h3 id="analytics-collections-heading" className="text-base font-semibold">
            Collection performance
          </h3>
          <p className="text-sm text-muted-foreground">
            Server-side sort and pagination · {formatCount(data.totalCount)} collections
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Select
            value={state.collectionSort}
            onValueChange={(value) =>
              onChange({
                collectionSort: value as AnalyticsUrlState['collectionSort'],
                collectionPage: 1,
              })
            }
          >
            <SelectTrigger className="w-[180px]" aria-label="Sort collections">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="VIEWS">Views</SelectItem>
              <SelectItem value="CLICKS">Clicks</SelectItem>
              <SelectItem value="ATTRIBUTED_REVENUE">Attributed revenue</SelectItem>
              <SelectItem value="RECENT">Recent activity</SelectItem>
            </SelectContent>
          </Select>
          <Select
            value={state.collectionDirection}
            onValueChange={(value) =>
              onChange({
                collectionDirection: value as AnalyticsUrlState['collectionDirection'],
                collectionPage: 1,
              })
            }
          >
            <SelectTrigger className="w-[120px]" aria-label="Collection sort direction">
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
              <TableHead scope="col">Collection</TableHead>
              <TableHead scope="col">Status</TableHead>
              <TableHead scope="col" className="text-right">
                Views
              </TableHead>
              <TableHead scope="col" className="text-right">
                Visitors
              </TableHead>
              <TableHead scope="col" className="text-right">
                Item clicks
              </TableHead>
              <TableHead scope="col" className="text-right">
                CTR
              </TableHead>
              <TableHead scope="col" className="text-right">
                Checkout starts
              </TableHead>
              <TableHead scope="col" className="text-right">
                Completed orders
              </TableHead>
              <TableHead scope="col" className="text-right">
                Attributed revenue
              </TableHead>
              <TableHead scope="col">Top clicked assets</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {data.items.length === 0 ? (
              <TableRow>
                <TableCell colSpan={10} className="py-8 text-center text-muted-foreground">
                  No collections in this range.
                </TableCell>
              </TableRow>
            ) : (
              data.items.map((item) => (
                <TableRow key={item.collectionId}>
                  <TableCell className="max-w-[14rem] truncate font-medium">{item.title}</TableCell>
                  <TableCell>{collectionStatusLabel(item.status)}</TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatNullableCount(item.views)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatNullableCount(item.uniqueVisitors)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatNullableCount(item.itemClicks)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatRatePercent(item.clickThroughRate)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatCount(item.attributedCheckoutStarts)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatCount(item.attributedCompletedOrders)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatMoneyCents(item.attributedGrossRevenueCents, data.currency)}
                  </TableCell>
                  <TableCell className="max-w-[16rem] text-sm text-muted-foreground">
                    {item.topClickedAssets == null
                      ? 'Unavailable'
                      : item.topClickedAssets.length === 0
                        ? 'No clicks'
                        : item.topClickedAssets
                            .slice(0, 3)
                            .map((asset) => `${asset.title} (${formatCount(asset.clicks)})`)
                            .join(' · ')}
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
                {formatCount(data.totalCount)} · sorted by{' '}
                {collectionSortLabel(state.collectionSort)}{' '}
                {state.collectionDirection === 'DESC' ? '↓' : '↑'}
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
              disabled={paginationDisabled || state.collectionPage <= 1}
              onClick={() => onChange({ collectionPage: state.collectionPage - 1 })}
              aria-label="Previous collections page"
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
              disabled={paginationDisabled || state.collectionPage >= totalPages}
              onClick={() => onChange({ collectionPage: state.collectionPage + 1 })}
              aria-label="Next collections page"
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
