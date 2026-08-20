import type { Metadata } from 'next'

import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { SiteFooter } from '@/components/site-footer'
import { SiteHeader } from '@/components/site-header'
import { AnalyticsBundleDetailView } from '@/components/sell/analytics/analytics-bundle-detail-view'

interface PageProps {
  params: Promise<{ id: string }>
}

export const metadata: Metadata = {
  title: 'Bundle analytics · AssetBlock',
  description: 'Engagement and commerce analytics for a seller bundle.',
}

export default async function SellAnalyticsBundleDetailPage({ params }: PageProps) {
  const { id } = await params

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <SiteMain>
        <SitePageContainer variant="document" padding="document">
          <AnalyticsBundleDetailView bundleId={id} />
        </SitePageContainer>
      </SiteMain>
      <SiteFooter />
    </div>
  )
}
