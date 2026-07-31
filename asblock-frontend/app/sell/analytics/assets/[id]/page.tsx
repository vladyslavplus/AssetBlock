import type { Metadata } from 'next'

import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { SiteFooter } from '@/components/site-footer'
import { SiteHeader } from '@/components/site-header'
import { AnalyticsAssetDetailView } from '@/components/sell/analytics/analytics-asset-detail-view'

interface PageProps {
  params: Promise<{ id: string }>
}

export const metadata: Metadata = {
  title: 'Asset analytics · AssetBlock',
  description: 'Engagement and commerce analytics for a seller asset.',
}

export default async function SellAnalyticsAssetDetailPage({ params }: PageProps) {
  const { id } = await params

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <SiteMain>
        <SitePageContainer variant="document" padding="document">
          <AnalyticsAssetDetailView assetId={id} />
        </SitePageContainer>
      </SiteMain>
      <SiteFooter />
    </div>
  )
}
