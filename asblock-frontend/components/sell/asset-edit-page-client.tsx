'use client'

import Link from 'next/link'
import { useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/components/auth/auth-context'
import {
  EmailVerificationNotice,
  isEmailVerified,
} from '@/components/auth/email-verification-notice'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'
import { SellQueryError } from '@/components/sell/sell-query-error'
import { AssetEditForm } from './asset-edit-form'
import { fetchSellerAssetDetailQuery, sellerKeys } from '@/lib/seller/seller-query'
import type { SellerAssetDetail } from '@/lib/seller/seller-asset-schemas'
import { runQueryInBackground } from '@/lib/query/query-refresh'
import { routes } from '@/lib/routes'

export function AssetEditPageClient({
  assetId,
  initialAsset,
}: {
  assetId: string
  initialAsset: SellerAssetDetail | null
}) {
  const { user, status } = useAuth()
  const authed = status === 'authenticated'

  const detailQuery = useQuery({
    queryKey: sellerKeys.detail(assetId),
    queryFn: ({ signal }) => fetchSellerAssetDetailQuery({ assetId, signal }),
    enabled: authed,
    initialData: initialAsset ?? undefined,
  })

  if (status === 'loading') {
    return <SessionBlockSkeleton className="py-12" lines={3} />
  }

  if (status === 'anonymous' || !user) {
    return (
      <div className="rounded-lg border border-border bg-card-elevated/50 px-4 py-8 text-center space-y-3 max-w-lg">
        <p className="text-sm text-muted-foreground">Sign in to edit your listings.</p>
        <Button asChild className="bg-primary text-primary-foreground hover:bg-[#6D28D9]">
          <Link href={routes.login(routes.sellerAssetEdit(assetId))}>Sign in</Link>
        </Button>
      </div>
    )
  }

  if (detailQuery.isPending) {
    return <SessionBlockSkeleton className="py-12" lines={3} />
  }

  if (detailQuery.isError) {
    const message = detailQuery.error instanceof Error ? detailQuery.error.message : ''
    if (message === 'NOT_FOUND') {
      return (
        <div className="rounded-lg border border-border bg-card-elevated/50 px-4 py-8 space-y-4 max-w-lg">
          <p className="text-sm text-foreground">This listing was not found.</p>
          <Button asChild variant="outline" className="border-border">
            <Link href="/sell">Back to Sell</Link>
          </Button>
        </div>
      )
    }
    return (
      <div className="max-w-lg">
        <SellQueryError
          title={
            message === 'SIGN_IN_REQUIRED'
              ? 'Please sign in to edit this listing.'
              : 'Could not load listing.'
          }
          onRetry={() => runQueryInBackground(detailQuery.refetch())}
          retrying={detailQuery.isRefetching}
        />
      </div>
    )
  }

  const asset = detailQuery.data
  if (!asset) {
    return <SessionBlockSkeleton className="py-12" lines={3} />
  }

  if (user.id.toLowerCase() !== asset.authorId.toLowerCase()) {
    return (
      <div className="rounded-lg border border-border bg-card-elevated/50 px-4 py-8 space-y-4 max-w-lg">
        <p className="text-sm text-foreground">Only the author can edit this asset.</p>
        <Button asChild variant="outline" className="border-border">
          <Link href="/sell">Back to Sell</Link>
        </Button>
      </div>
    )
  }

  if (!isEmailVerified(user)) {
    return <EmailVerificationNotice className="max-w-lg" />
  }

  return <AssetEditForm asset={asset} />
}
