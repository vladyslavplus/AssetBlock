import type { Metadata } from 'next'
import { LibraryPageClient } from '@/components/library/library-page-client'

export const metadata: Metadata = {
  title: 'My library - AssetBlock',
  description: 'View and manage your digital asset purchases.',
}

export default function LibraryPage() {
  return <LibraryPageClient />
}
