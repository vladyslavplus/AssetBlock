'use client'

import { useState } from 'react'
import Link from 'next/link'
import { CheckCircle2, Download, ExternalLink, Star } from 'lucide-react'

import { LeaveReviewDialog } from '@/components/reviews/leave-review-dialog'
import { Button } from '@/components/ui/button'
import { formatUsdWhole } from '@/lib/format-currency'
import { formatLongDate } from '@/lib/format-date'
import type { PurchaseLibraryItem } from '@/lib/library/purchase-types'
import { isWithinReviewWindowAfterPurchase } from '@/lib/reviews/review-constants'

interface LibraryPurchaseCardProps {
  purchase: PurchaseLibraryItem
}

export function LibraryPurchaseCard({ purchase }: LibraryPurchaseCardProps) {
  const [dialogOpen, setDialogOpen] = useState(false)
  const [reviewSubmitted, setReviewSubmitted] = useState(false)

  const withinReviewWindow = isWithinReviewWindowAfterPurchase(purchase.purchasedAt)
  const alreadyReviewed = purchase.hasUserReviewed || reviewSubmitted

  return (
    <div className="bg-card-elevated border border-border rounded-xl p-4 space-y-3">
      <h2 className="font-semibold text-foreground line-clamp-2">{purchase.assetTitle}</h2>

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

      <div className="flex items-center justify-between text-xs text-muted-foreground">
        <span className="font-semibold text-foreground">
          {formatUsdWhole(Number(purchase.price))}
        </span>
        <span>{formatLongDate(purchase.purchasedAt)}</span>
      </div>

      <div className="flex flex-col gap-2 pt-2 border-t border-border/30">
        <div className="flex gap-2">
          <Button
            asChild
            size="sm"
            className="flex-1 bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium text-xs h-8"
          >
            <Link href={`/api/assets/${purchase.assetId}/download`}>
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
