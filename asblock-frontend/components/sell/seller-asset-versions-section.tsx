'use client'

import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Eye, Loader2, PencilLine } from 'lucide-react'
import { AssetVersionHistory } from '@/components/assets/asset-version-history'
import { PublishVersionForm } from '@/components/sell/publish-version-form'
import { AssetProcessingStatusPanel } from '@/components/sell/asset-processing-status-panel'
import { VersionChecksPanel } from '@/components/sell/version-checks-panel'
import { SourceDeclarationEditor } from '@/components/sell/source-declaration-editor'
import { Button } from '@/components/ui/button'
import { fetchSellerAssetVersions } from '@/lib/seller/seller-api'
import { sellerKeys } from '@/lib/seller/seller-query'

interface SellerAssetVersionsSectionProps {
  assetId: string
  uploadBlockers?: string[]
}

export function SellerAssetVersionsSection({
  assetId,
  uploadBlockers = [],
}: SellerAssetVersionsSectionProps) {
  const versionsQuery = useQuery({
    queryKey: sellerKeys.versions(assetId),
    queryFn: () => fetchSellerAssetVersions(assetId),
  })
  const [checksVersionId, setChecksVersionId] = useState<string | null>(null)
  const [editingVersionId, setEditingVersionId] = useState<string | null>(null)

  const versions = versionsQuery.data ?? []
  const loading = versionsQuery.isPending
  const error = versionsQuery.error instanceof Error ? versionsQuery.error.message : null

  return (
    <div className="space-y-6 pt-6 border-t border-border">
      <AssetProcessingStatusPanel assetId={assetId} versions={versions} />

      <PublishVersionForm assetId={assetId} blockers={uploadBlockers} />

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
        ) : versions.length === 0 ? (
          <p className="text-sm text-muted-foreground py-2">
            No package uploaded yet. This draft only holds metadata — upload the first version above
            when you are ready.
          </p>
        ) : (
          <div className="space-y-3">
            <AssetVersionHistory versions={versions} showHashes />
            {versions.map((version) => (
              <div key={version.id} className="space-y-2">
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="border-border"
                  onClick={() =>
                    setChecksVersionId((current) => (current === version.id ? null : version.id))
                  }
                >
                  <Eye className="mr-1.5 size-3.5" aria-hidden />
                  {checksVersionId === version.id ? 'Hide checks' : 'View checks'}
                </Button>
                {checksVersionId === version.id ? (
                  <VersionChecksPanel assetId={assetId} versionId={version.id} />
                ) : null}
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="border-border"
                  onClick={() =>
                    setEditingVersionId((current) => (current === version.id ? null : version.id))
                  }
                >
                  <PencilLine className="mr-1.5 size-3.5" aria-hidden />
                  {editingVersionId === version.id ? 'Hide sources' : 'Edit sources'}
                </Button>
                {editingVersionId === version.id ? (
                  <div className="rounded-md border border-border p-3">
                    <p className="text-xs text-muted-foreground mb-2">
                      These sources belong to this uploaded version. They do not change the
                      declaration of the next upload.
                    </p>
                    <SourceDeclarationEditor assetId={assetId} versionId={version.id} />
                  </div>
                ) : null}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
