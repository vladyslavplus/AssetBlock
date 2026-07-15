'use client'

import { useQuery } from '@tanstack/react-query'
import Link from 'next/link'
import { ArrowLeft } from 'lucide-react'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { AssetDetailHero } from '@/components/assets/asset-detail-hero'
import { AssetPurchaseCard } from '@/components/assets/asset-purchase-card'
import { AssetReviewsList } from '@/components/assets/asset-reviews-list'
import type { AssetDetailItemApi } from '@/lib/catalog/assets-api'
import { mapDetailApiToListItemForHero } from '@/lib/catalog/assets-api'
import {
  assetKeys,
  fetchAssetDetailPublic,
  fetchAssetReviewsPublic,
} from '@/lib/catalog/asset-detail-query'
import type { AssetReview } from '@/lib/catalog/catalog-utils'

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

  const asset = mapDetailApiToListItemForHero(detailQuery.data)
  const reviews = reviewsQuery.data ?? []

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
