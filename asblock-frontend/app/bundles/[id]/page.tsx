import { notFound } from 'next/navigation'
import { SiteHeader } from '@/components/site-header'
import { SiteFooter } from '@/components/site-footer'
import { BundleDetailView } from '@/components/bundles/bundle-detail-view'
import { getBundleDetailCached } from '@/lib/server/bundle-detail-server'
import { fetchPaymentsCapabilitiesServer } from '@/lib/server/payments-capabilities'

interface BundleDetailPageProps {
  params: Promise<{ id: string }>
}

const UUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i

export async function generateMetadata({ params }: BundleDetailPageProps) {
  const { id } = await params
  if (!UUID_REGEX.test(id.trim())) {
    return {
      title: 'Bundle Not Found · AssetBlock',
      description: 'The requested bundle was not found on AssetBlock.',
    }
  }

  const result = await getBundleDetailCached(id)
  if (result.status === 'not_found') {
    return {
      title: 'Bundle Not Found · AssetBlock',
      description: 'The requested bundle was not found on AssetBlock.',
    }
  }

  if (result.status === 'success') {
    const { bundle } = result
    const description =
      bundle.description?.trim().slice(0, 160) ||
      `Get ${bundle.title} and save on curated assets on AssetBlock.`

    return {
      title: `${bundle.title} · AssetBlock`,
      description,
    }
  }

  return {
    title: 'Bundle · AssetBlock',
    description: 'Curated asset bundles on AssetBlock.',
  }
}

export default async function BundleDetailPage({ params }: BundleDetailPageProps) {
  const { id } = await params
  if (!UUID_REGEX.test(id.trim())) {
    notFound()
  }

  // Fetch bundle detail and payments capabilities in parallel on the server
  const [bundleResult, { checkoutConfigured }] = await Promise.all([
    getBundleDetailCached(id),
    fetchPaymentsCapabilitiesServer().catch(() => ({ checkoutConfigured: false })),
  ])

  // Only a confirmed 404 triggers the notFound() boundary.
  // Transient upstream failures (5xx, timeout) fall back to client query retry without failing the server render.
  if (bundleResult.status === 'not_found') {
    notFound()
  }

  const initialBundle = bundleResult.status === 'success' ? bundleResult.bundle : undefined

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <BundleDetailView
        bundleId={id}
        initialBundle={initialBundle}
        checkoutConfigured={checkoutConfigured}
      />
      <SiteFooter />
    </div>
  )
}
