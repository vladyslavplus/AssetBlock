import { SiteHeader } from '@/components/site-header'
import { SiteFooter } from '@/components/site-footer'
import { BundleDetailView } from '@/components/bundles/bundle-detail-view'
import { fetchPaymentsCapabilitiesServer } from '@/lib/server/payments-capabilities'

interface BundleDetailPageProps {
  params: Promise<{ id: string }>
}

export async function generateMetadata({ params }: BundleDetailPageProps) {
  const { id } = await params
  return {
    title: `Bundle · AssetBlock`,
    description: `View bundle ${id} on AssetBlock.`,
  }
}

export default async function BundleDetailPage({ params }: BundleDetailPageProps) {
  const { id } = await params
  const { checkoutConfigured } = await fetchPaymentsCapabilitiesServer()

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <BundleDetailView bundleId={id} checkoutConfigured={checkoutConfigured} />
      <SiteFooter />
    </div>
  )
}
