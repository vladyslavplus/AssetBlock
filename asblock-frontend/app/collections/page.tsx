import type { Metadata } from 'next'
import { CollectionsBrowsePage } from '@/components/collections/collections-browse-page'

export const metadata: Metadata = {
  title: 'Collections · AssetBlock',
  description: 'Browse editorial asset collections curated by sellers on AssetBlock.',
}

export default function CollectionsPage() {
  return <CollectionsBrowsePage />
}
