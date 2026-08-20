'use client'

import Link from 'next/link'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'next/navigation'
import { ArrowLeft } from 'lucide-react'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { BundlePurchaseCard } from '@/components/bundles/bundle-purchase-card'
import { Badge } from '@/components/ui/badge'
import { useAnalyticsPageView } from '@/hooks/use-analytics-page-view'
import {
  appendAnalyticsQuery,
  buildCheckoutAttributionFromPage,
  buildPurchaseReturnPath,
  resolveTrafficSourceFromLocation,
} from '@/lib/analytics/telemetry-source'
import { ApiRequestError } from '@/lib/http/api-client'
import { bundleKeys, fetchBundleDetailQuery } from '@/lib/bundles/bundles-query'
import { formatUsdWhole } from '@/lib/format-currency'

interface BundleDetailViewProps {
  bundleId: string
  checkoutConfigured: boolean
}

export function BundleDetailView({ bundleId, checkoutConfigured }: BundleDetailViewProps) {
  const searchParams = useSearchParams()
  const trafficSource = resolveTrafficSourceFromLocation(searchParams)
  const checkoutAttribution = buildCheckoutAttributionFromPage(searchParams)

  useAnalyticsPageView(`bundle-view:${bundleId}`, {
    eventType: 'BUNDLE_VIEW',
    bundleId,
    source: trafficSource,
  })

  const detailQuery = useQuery({
    queryKey: bundleKeys.publicDetail(bundleId),
    queryFn: () => fetchBundleDetailQuery(bundleId),
    retry: (count, err) => {
      if (err instanceof ApiRequestError && err.status === 404) return false
      return count < 2
    },
  })

  if (detailQuery.isPending) {
    return (
      <SiteMain>
        <SitePageContainer variant="wide" padding="none">
          <p className="text-sm text-muted-foreground py-8">Loading bundle…</p>
        </SitePageContainer>
      </SiteMain>
    )
  }

  if (detailQuery.isError) {
    const notFound =
      detailQuery.error instanceof ApiRequestError && detailQuery.error.status === 404
    return (
      <SiteMain>
        <SitePageContainer variant="wide" padding="none">
          <Link
            href="/bundles"
            className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors mb-6"
          >
            <ArrowLeft className="w-4 h-4" />
            Back to bundles
          </Link>
          <p className="text-sm text-destructive" role="alert">
            {notFound
              ? 'This bundle is not available.'
              : detailQuery.error instanceof Error
                ? detailQuery.error.message
                : 'Could not load bundle.'}
          </p>
        </SitePageContainer>
      </SiteMain>
    )
  }

  const detail = detailQuery.data
  const items = [...detail.items].sort((a, b) => a.position - b.position)

  return (
    <SiteMain>
      <SitePageContainer variant="wide" padding="none">
        <Link
          href="/bundles"
          className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors mb-6"
        >
          <ArrowLeft className="w-4 h-4" />
          Back to bundles
        </Link>

        <div className="grid grid-cols-1 lg:grid-cols-3 gap-8">
          <div className="lg:col-span-2 space-y-6">
            <div className="space-y-2">
              <div className="flex flex-wrap gap-2">
                <Badge variant="secondary" className="text-[10px]">
                  Bundle
                </Badge>
                <Badge variant="secondary" className="text-[10px]">
                  Rev {detail.revisionNumber}
                </Badge>
              </div>
              <h1 className="text-3xl font-semibold text-balance">{detail.title}</h1>
              <p className="text-sm text-muted-foreground">
                By{' '}
                <Link
                  href={`/users/${encodeURIComponent(detail.sellerUsername)}`}
                  className="text-accent hover:underline"
                >
                  @{detail.sellerUsername}
                </Link>
              </p>
              {detail.description?.trim() ? (
                <p className="text-sm text-foreground leading-relaxed whitespace-pre-wrap pt-2">
                  {detail.description}
                </p>
              ) : null}
            </div>

            <div className="space-y-3">
              <h2 className="text-lg font-semibold text-foreground">Included assets</h2>
              <ul className="space-y-3">
                {items.map((item, index) => (
                  <li
                    key={item.assetId ?? `${item.title}-${index}`}
                    className="rounded-lg border border-border bg-card-elevated/40 px-4 py-3 space-y-1"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-1">
                      <p className="font-medium text-foreground line-clamp-2">{item.title}</p>
                      <span className="text-xs font-mono text-muted-foreground shrink-0">
                        {formatUsdWhole(item.listPrice)}
                      </span>
                    </div>
                    <p className="text-xs text-muted-foreground">
                      {item.currentVersionNumber != null ? `v${item.currentVersionNumber}` : null}
                      {item.licenseDisplayName
                        ? `${item.currentVersionNumber != null ? ' · ' : ''}${item.licenseDisplayName}`
                        : null}
                      {!item.isAvailable && item.unavailableReason
                        ? ` · ${item.unavailableReason}`
                        : null}
                    </p>
                    {item.assetId && item.isAvailable ? (
                      <Link
                        href={appendAnalyticsQuery(`/assets/${item.assetId}`, 'bundle_page')}
                        className="text-xs font-medium text-primary hover:text-primary/80"
                      >
                        View asset →
                      </Link>
                    ) : null}
                  </li>
                ))}
              </ul>
            </div>
          </div>

          <div className="min-w-0 lg:col-span-1">
            <div className="lg:sticky lg:top-24">
              <BundlePurchaseCard
                bundleId={detail.id}
                sellerId={detail.sellerId}
                title={detail.title}
                price={detail.price}
                listPriceTotal={detail.listPriceTotal}
                savingsAmount={detail.savingsAmount}
                savingsPercent={detail.savingsPercent}
                isAvailable={detail.isAvailable}
                items={items}
                checkoutConfigured={checkoutConfigured}
                returnPath={buildPurchaseReturnPath(`/bundles/${bundleId}`, searchParams)}
                checkoutAttribution={checkoutAttribution}
              />
            </div>
          </div>
        </div>
      </SitePageContainer>
    </SiteMain>
  )
}
