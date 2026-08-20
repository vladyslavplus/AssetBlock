import type { Metadata } from 'next'
import { BundlesBrowsePage } from '@/components/bundles/bundles-browse-page'

export const metadata: Metadata = {
  title: 'Bundles · AssetBlock',
  description: 'Browse discounted asset bundles on AssetBlock.',
}

export default function BundlesPage() {
  return <BundlesBrowsePage />
}
