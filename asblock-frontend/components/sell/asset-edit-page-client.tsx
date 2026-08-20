'use client'

import Link from 'next/link'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/components/auth/auth-context'
import {
  EmailVerificationNotice,
  isEmailVerified,
} from '@/components/auth/email-verification-notice'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'
import type { AssetDetailItemApi } from '@/lib/catalog/assets-api'
import { AssetEditForm } from './asset-edit-form'

export function AssetEditPageClient({ initialAsset }: { initialAsset: AssetDetailItemApi }) {
  const { user, status } = useAuth()

  if (status === 'loading') {
    return <SessionBlockSkeleton className="py-12" lines={3} />
  }

  if (status === 'anonymous' || !user) {
    return (
      <div className="rounded-lg border border-border bg-card-elevated/50 px-4 py-8 text-center space-y-3 max-w-lg">
        <p className="text-sm text-muted-foreground">Sign in to edit your listings.</p>
        <Button asChild className="bg-primary text-primary-foreground hover:bg-[#6D28D9]">
          <Link href={`/login?returnUrl=/sell/assets/${initialAsset.id}/edit`}>Sign in</Link>
        </Button>
      </div>
    )
  }

  if (user.id.toLowerCase() !== initialAsset.authorId.toLowerCase()) {
    return (
      <div className="rounded-lg border border-border bg-card-elevated/50 px-4 py-8 space-y-4 max-w-lg">
        <p className="text-sm text-foreground">Only the author can edit this asset.</p>
        <Button asChild variant="outline" className="border-border">
          <Link href={`/assets/${initialAsset.id}`}>View listing</Link>
        </Button>
      </div>
    )
  }

  if (!isEmailVerified(user)) {
    return <EmailVerificationNotice className="max-w-lg" />
  }

  return <AssetEditForm initialAsset={initialAsset} />
}
