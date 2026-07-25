import { SiteHeader } from '@/components/site-header'
import { SiteFooter } from '@/components/site-footer'
import { CollectionDetailView } from '@/components/collections/collection-detail-view'

interface CollectionDetailPageProps {
  params: Promise<{ id: string }>
}

export async function generateMetadata({ params }: CollectionDetailPageProps) {
  const { id } = await params
  return {
    title: `Collection · AssetBlock`,
    description: `View collection ${id} on AssetBlock.`,
  }
}

export default async function CollectionDetailPage({ params }: CollectionDetailPageProps) {
  const { id } = await params
  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <CollectionDetailView collectionId={id} />
      <SiteFooter />
    </div>
  )
}
