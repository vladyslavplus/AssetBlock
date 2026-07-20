'use client'

import { useQuery } from '@tanstack/react-query'
import Link from 'next/link'
import { ArrowLeft } from 'lucide-react'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { AssetDetailHero } from '@/components/assets/asset-detail-hero'
import { AssetLicenseCard } from '@/components/assets/asset-license-card'
import { AssetPurchaseCard } from '@/components/assets/asset-purchase-card'
import { AssetReviewsList } from '@/components/assets/asset-reviews-list'
import { AssetVersionHistory } from '@/components/assets/asset-version-history'
import { ContentHashDisplay } from '@/components/assets/content-hash-display'
import type { AssetDetailItemApi } from '@/lib/catalog/assets-api'
import { mapDetailApiToListItemForHero } from '@/lib/catalog/assets-api'
import {
  assetKeys,
  fetchAssetDetailPublic,
  fetchAssetReviewsPublic,
  fetchAssetVersionsPublic,
} from '@/lib/catalog/asset-detail-query'
import { formatBytes } from '@/lib/format-bytes'
import { formatShortMonthDate } from '@/lib/format-date'
import type { AssetReview } from '@/lib/catalog/catalog-utils'
import { Badge } from '@/components/ui/badge'

interface AssetDetailViewProps {
  assetId: string
  initialDetail: AssetDetailItemApi
  initialReviews: AssetReview[]
  checkoutConfigured: boolean
}

export function AssetDetailView({
  assetId,
  initialDetail,
  initialReviews,
  checkoutConfigured,
}: AssetDetailViewProps) {
  const detailQuery = useQuery({
    queryKey: assetKeys.detail(assetId),
    queryFn: () => fetchAssetDetailPublic(assetId),
    initialData: initialDetail,
  })

  const reviewsQuery = useQuery({
    queryKey: assetKeys.reviews(assetId),
    queryFn: () => fetchAssetReviewsPublic(assetId),
    initialData: initialReviews,
  })

  const versionsQuery = useQuery({
    queryKey: assetKeys.versions(assetId),
    queryFn: () => fetchAssetVersionsPublic(assetId),
  })

  const detail = detailQuery.data
  const asset = mapDetailApiToListItemForHero(detail)
  const reviews = reviewsQuery.data ?? []
  const versions = versionsQuery.data ?? []

  return (
    <SiteMain>
      <SitePageContainer variant="wide" padding="none">
        <Link
          href="/assets"
          className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors mb-6"
        >
          <ArrowLeft className="w-4 h-4" />
          Back to catalog
        </Link>

        <div className="grid grid-cols-1 lg:grid-cols-3 gap-8">
          <div className="lg:col-span-2 flex min-w-0 flex-col gap-8">
            <AssetDetailHero asset={asset} />

            <div className="flex min-w-0 flex-col gap-2">
              <h2 className="text-lg font-semibold text-foreground">Description</h2>
              <p className="min-w-0 max-w-full whitespace-pre-wrap break-words text-sm leading-relaxed text-foreground [overflow-wrap:anywhere]">
                {asset.description?.trim() ? (
                  asset.description
                ) : (
                  <span className="text-muted-foreground">No description provided yet.</span>
                )}
              </p>
            </div>

            <div className="space-y-3">
              <h2 className="text-lg font-semibold text-foreground">Current version</h2>
              <div className="rounded-lg border border-border bg-card-elevated/30 px-4 py-3 space-y-2">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-sm font-semibold text-foreground">
                    v{detail.currentVersionNumber}
                  </span>
                  <Badge variant="secondary" className="text-[10px]">
                    {detail.currentLicense.displayName}
                  </Badge>
                  <span className="text-xs text-muted-foreground">
                    {formatShortMonthDate(detail.currentVersionCreatedAt)}
                  </span>
                </div>
                <p className="text-xs text-muted-foreground">
                  {detail.currentFileName} · {formatBytes(detail.currentContentLength)}
                </p>
              </div>
              <AssetLicenseCard license={detail.currentLicense} />
              <ContentHashDisplay hash={detail.currentContentSha256} />
            </div>

            <div className="space-y-3">
              <h2 className="text-lg font-semibold text-foreground">Version history</h2>
              {versionsQuery.isPending ? (
                <p className="text-sm text-muted-foreground">Loading version history…</p>
              ) : versionsQuery.isError ? (
                <p className="text-sm text-muted-foreground">Version history is unavailable.</p>
              ) : (
                <AssetVersionHistory versions={versions} />
              )}
            </div>

            <AssetReviewsList reviews={reviews} />
          </div>

          <div className="min-w-0 lg:col-span-1">
            <div className="lg:sticky lg:top-24">
              <AssetPurchaseCard
                assetId={asset.id}
                authorId={asset.authorId}
                title={asset.title}
                price={asset.price}
                checkoutConfigured={checkoutConfigured}
                returnPath={`/assets/${assetId}`}
              />
            </div>
          </div>
        </div>
      </SitePageContainer>
    </SiteMain>
  )
}
