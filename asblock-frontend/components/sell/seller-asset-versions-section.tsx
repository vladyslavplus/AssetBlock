'use client'

import { useQuery } from '@tanstack/react-query'
import { Loader2 } from 'lucide-react'
import { AssetVersionHistory } from '@/components/assets/asset-version-history'
import { PublishVersionForm } from '@/components/sell/publish-version-form'
import { fetchSellerAssetVersions } from '@/lib/seller/seller-api'
import { sellerKeys } from '@/lib/seller/seller-query'

interface SellerAssetVersionsSectionProps {
  assetId: string
}

export function SellerAssetVersionsSection({ assetId }: SellerAssetVersionsSectionProps) {
  const versionsQuery = useQuery({
    queryKey: sellerKeys.versions(assetId),
    queryFn: () => fetchSellerAssetVersions(assetId),
  })

  const versions = versionsQuery.data ?? []
  const loading = versionsQuery.isPending
  const error = versionsQuery.error instanceof Error ? versionsQuery.error.message : null

  return (
    <div className="space-y-6 pt-6 border-t border-border">
      <PublishVersionForm assetId={assetId} />

      <div className="space-y-3">
        <h3 className="text-sm font-semibold text-foreground">Version history</h3>
        {loading ? (
          <div className="flex items-center gap-2 text-sm text-muted-foreground py-4">
            <Loader2 className="size-4 animate-spin" aria-hidden />
            Loading versions…
          </div>
        ) : error ? (
          <p className="text-sm text-destructive" role="alert">
            {error}
          </p>
        ) : (
          <AssetVersionHistory versions={versions} showHashes />
        )}
      </div>
    </div>
  )
}
