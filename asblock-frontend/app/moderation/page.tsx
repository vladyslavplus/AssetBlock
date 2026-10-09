import type { Metadata } from 'next'
import { ShieldAlert } from 'lucide-react'
import { QueryEmptyState } from '@/components/shared/query-empty-state'

export const dynamic = 'force-dynamic'

export const metadata: Metadata = {
  title: 'Moderation · AssetBlock',
  description: 'Moderation foundation for reviewed asset publication.',
}

export default function ModerationPage() {
  return (
    <div className="max-w-2xl">
      <QueryEmptyState
        icon={ShieldAlert}
        title="Moderation workflow is not available yet"
        description={
          <>
            <span className="block">
              The review queue, case handling, and decision tools are not built yet. Publication
              still requires a completed human review, so no candidate version can be approved or
              published from here.
            </span>
            <span className="block">
              This area will provide the case queue and decision workflow in a later update.
            </span>
          </>
        }
      />
    </div>
  )
}
