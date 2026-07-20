'use client'

import { useState } from 'react'
import Link from 'next/link'
import { useQuery } from '@tanstack/react-query'
import { CheckCircle2, ChevronDown, Download, ExternalLink, Loader2, Star } from 'lucide-react'

import { LeaveReviewDialog } from '@/components/reviews/leave-review-dialog'
import { AssetVersionHistory } from '@/components/assets/asset-version-history'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'
import { Label } from '@/components/ui/label'
import { buildAssetDownloadUrl } from '@/lib/assets/download-url'
import { formatUsdWhole } from '@/lib/format-currency'
import { formatLongDate } from '@/lib/format-date'
import { fetchLibraryAssetVersions, libraryKeys } from '@/lib/library/library-query'
import type { PurchaseLibraryItem } from '@/lib/library/purchase-types'
import { isWithinReviewWindowAfterPurchase } from '@/lib/reviews/review-constants'
import { cn } from '@/lib/utils'

interface LibraryPurchaseCardProps {
  purchase: PurchaseLibraryItem
}

export function LibraryPurchaseCard({ purchase }: LibraryPurchaseCardProps) {
  const [dialogOpen, setDialogOpen] = useState(false)
  const [reviewSubmitted, setReviewSubmitted] = useState(false)
  const [historyOpen, setHistoryOpen] = useState(false)

  const versionsQuery = useQuery({
    queryKey: libraryKeys.assetVersions(purchase.assetId),
    queryFn: ({ signal }) => fetchLibraryAssetVersions(purchase.assetId, signal),
    enabled: historyOpen,
  })

  const withinReviewWindow = isWithinReviewWindowAfterPurchase(purchase.purchasedAt)
  const alreadyReviewed = purchase.hasUserReviewed || reviewSubmitted

  const downloadOptions: Array<{ versionId: string; label: string }> = []
  if (purchase.latestEntitledVersionId) {
    downloadOptions.push({
      versionId: purchase.latestEntitledVersionId,
      label: `Latest (v${purchase.latestEntitledVersionNumber})`,
    })
  }
  if (
    purchase.purchasedVersionId &&
    purchase.purchasedVersionId !== purchase.latestEntitledVersionId
  ) {
    downloadOptions.push({
      versionId: purchase.purchasedVersionId,
      label: `Purchased (v${purchase.purchasedVersionNumber})`,
    })
  }

  const [selectedVersionId, setSelectedVersionId] = useState(
    () => downloadOptions[0]?.versionId ?? '',
  )

  const effectiveVersionId =
    downloadOptions.find((o) => o.versionId === selectedVersionId)?.versionId ??
    downloadOptions[0]?.versionId

  const downloadHref = buildAssetDownloadUrl(purchase.assetId, effectiveVersionId)
  const priceLabel = formatUsdWhole(Number(purchase.pricePaid))

  return (
    <div className="bg-card-elevated border border-border rounded-xl p-4 space-y-3">
      <div className="flex items-start justify-between gap-2">
        <h2 className="font-semibold text-foreground line-clamp-2">{purchase.assetTitle}</h2>
        {purchase.hasUpdate ? (
          <Badge className="shrink-0 text-[10px]">Update available</Badge>
        ) : null}
      </div>

      <div className="flex items-center gap-2">
        <span className="text-xs text-muted-foreground">
          by{' '}
          <Link
            href={`/users/${encodeURIComponent(purchase.authorUsername)}`}
            className="font-mono text-muted-foreground/80 hover:text-accent transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 rounded-sm"
          >
            @{purchase.authorUsername}
          </Link>
        </span>
      </div>

      <div className="grid grid-cols-1 gap-1 text-xs text-muted-foreground">
        <p>
          Purchased version:{' '}
          <span className="font-medium text-foreground">v{purchase.purchasedVersionNumber}</span>
        </p>
        <p>
          Latest entitled:{' '}
          <span className="font-medium text-foreground">
            v{purchase.latestEntitledVersionNumber}
          </span>
        </p>
      </div>

      <div className="flex items-center justify-between text-xs text-muted-foreground">
        <span className="font-semibold text-foreground">{priceLabel}</span>
        <span>{formatLongDate(purchase.purchasedAt)}</span>
      </div>

      <div className="flex flex-col gap-2 pt-2 border-t border-border/30">
        {downloadOptions.length > 1 ? (
          <div className="space-y-1.5">
            <Label htmlFor={`library-version-${purchase.id}`} className="text-[11px]">
              Download version
            </Label>
            <select
              id={`library-version-${purchase.id}`}
              className="border-input bg-input h-8 w-full rounded-md border px-2 text-xs shadow-xs outline-none focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]"
              value={effectiveVersionId}
              onChange={(e) => setSelectedVersionId(e.target.value)}
            >
              {downloadOptions.map((option) => (
                <option key={option.versionId} value={option.versionId}>
                  {option.label}
                </option>
              ))}
            </select>
          </div>
        ) : null}

        <div className="flex gap-2">
          <Button
            asChild
            size="sm"
            className="flex-1 bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium text-xs h-8"
          >
            <Link href={downloadHref}>
              <Download className="w-3.5 h-3.5 mr-1.5" />
              Download
            </Link>
          </Button>
          <Button
            asChild
            variant="outline"
            size="sm"
            className="flex-1 border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground transition-smooth font-medium text-xs h-8"
          >
            <Link href={`/assets/${purchase.assetId}`}>
              <ExternalLink className="w-3.5 h-3.5 mr-1.5" />
              View asset
            </Link>
          </Button>
        </div>

        {withinReviewWindow && alreadyReviewed && (
          <Button
            type="button"
            variant="secondary"
            size="sm"
            disabled
            className="w-full text-xs h-8 font-medium border border-border/60 opacity-80 cursor-not-allowed"
            aria-label="You already left a review for this asset"
          >
            <CheckCircle2 className="w-3.5 h-3.5 mr-1.5 shrink-0" aria-hidden />
            Review submitted
          </Button>
        )}
        {withinReviewWindow && !alreadyReviewed && (
          <Button
            type="button"
            variant="secondary"
            size="sm"
            className="w-full text-xs h-8 font-medium border border-border/60"
            onClick={() => setDialogOpen(true)}
          >
            <Star className="w-3.5 h-3.5 mr-1.5" aria-hidden />
            Leave a review
          </Button>
        )}
      </div>

      <Collapsible open={historyOpen} onOpenChange={setHistoryOpen}>
        <CollapsibleTrigger className="flex w-full items-center justify-between gap-2 rounded-md border border-border/60 bg-secondary/20 px-3 py-2 text-xs text-foreground hover:bg-secondary/40 transition-colors">
          <span>Version history</span>
          <ChevronDown
            className={cn(
              'size-4 text-muted-foreground transition-transform',
              historyOpen && 'rotate-180',
            )}
            aria-hidden
          />
        </CollapsibleTrigger>
        <CollapsibleContent className="pt-3">
          {versionsQuery.isPending ? (
            <div className="flex items-center gap-2 text-xs text-muted-foreground py-2">
              <Loader2 className="size-3.5 animate-spin" aria-hidden />
              Loading…
            </div>
          ) : versionsQuery.isError ? (
            <p className="text-xs text-muted-foreground">Version history is unavailable.</p>
          ) : (
            <AssetVersionHistory versions={versionsQuery.data ?? []} />
          )}
        </CollapsibleContent>
      </Collapsible>

      <LeaveReviewDialog
        open={dialogOpen}
        onOpenChange={setDialogOpen}
        assetId={purchase.assetId}
        assetTitle={purchase.assetTitle}
        onSubmitted={() => setReviewSubmitted(true)}
      />
    </div>
  )
}
