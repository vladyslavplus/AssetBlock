import type { Metadata } from 'next'
import { ModerationQueue } from '@/components/moderation/moderation-queue'

export const dynamic = 'force-dynamic'

export const metadata: Metadata = {
  title: 'Moderation · AssetBlock',
  description: 'Review submitted asset versions.',
}

export default function ModerationPage() {
  return <ModerationQueue />
}
