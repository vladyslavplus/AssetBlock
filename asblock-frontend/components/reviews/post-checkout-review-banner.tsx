'use client'

import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import Link from 'next/link'
import { Star } from 'lucide-react'

import { LeaveReviewDialog } from '@/components/reviews/leave-review-dialog'
import { Button } from '@/components/ui/button'
import { apiFetch } from '@/lib/http/api-client'
import type { AssetDetailItemApi } from '@/lib/catalog/assets-api'
import { libraryKeys } from '@/lib/library/library-query'
import { PENDING_REVIEW_ASSET_ID_KEY } from '@/lib/reviews/review-constants'

function readPendingReviewAssetId(): string | null {
  try {
    const id = sessionStorage.getItem(PENDING_REVIEW_ASSET_ID_KEY)?.trim()
    return id && id.length > 0 ? id : null
  } catch {
    return null
  }
}

export function PostCheckoutReviewBanner() {
  const queryClient = useQueryClient()
  const libraryInvalidatedRef = useRef(false)
  /** Avoid SSR/client hydration mismatch: read sessionStorage only after mount. */
  const [storageReady, setStorageReady] = useState(false)
  const [title, setTitle] = useState('Your purchase')
  const [dismissed, setDismissed] = useState(false)
  const [dialogOpen, setDialogOpen] = useState(false)

  useEffect(() => {
    if (libraryInvalidatedRef.current) return
    libraryInvalidatedRef.current = true
    void queryClient.invalidateQueries({ queryKey: libraryKeys.purchases() })
  }, [queryClient])

  useEffect(() => {
    const frame = requestAnimationFrame(() => setStorageReady(true))
    return () => cancelAnimationFrame(frame)
  }, [])

  const assetId = storageReady ? readPendingReviewAssetId() : null

  useEffect(() => {
    if (!assetId) return
    let cancelled = false
    void (async () => {
      try {
        const detail = await apiFetch<AssetDetailItemApi>({
          path: `/api/assets/${encodeURIComponent(assetId)}`,
        })
        if (!cancelled && detail?.title?.trim()) {
          setTitle(detail.title.trim())
        }
      } catch {
        /* Title is optional; dialog still works. */
      }
    })()
    return () => {
      cancelled = true
    }
  }, [assetId])

  const clearPending = () => {
    try {
      sessionStorage.removeItem(PENDING_REVIEW_ASSET_ID_KEY)
    } catch {
      /* ignore */
    }
  }

  const handleDismiss = () => {
    clearPending()
    setDismissed(true)
  }

  const handleReviewSubmitted = () => {
    clearPending()
    setDismissed(true)
  }

  if (dismissed || !assetId) {
    return null
  }

  return (
    <>
      <div
        className="mb-6 rounded-lg border border-border bg-secondary/20 px-4 py-4 space-y-3"
        role="region"
        aria-label="Review your purchase"
      >
        <div className="flex gap-2">
          <Star className="size-5 shrink-0 text-yellow-500 fill-yellow-500/80" aria-hidden />
          <div className="min-w-0 space-y-1">
            <p className="text-sm font-medium text-foreground">How was your purchase?</p>
            <p className="text-xs text-muted-foreground leading-relaxed">
              Leave a quick rating for{' '}
              <span className="font-medium text-foreground break-words">{title}</span>. You can also
              do this later from{' '}
              <Link href="/library" className="text-accent underline-offset-2 hover:underline">
                My library
              </Link>
              .
            </p>
          </div>
        </div>
        <div className="flex flex-col sm:flex-row gap-2 sm:items-center">
          <Button
            type="button"
            size="sm"
            className="bg-primary text-primary-foreground hover:bg-[#6D28D9] font-medium"
            onClick={() => setDialogOpen(true)}
          >
            Rate now
          </Button>
          <Button
            type="button"
            size="sm"
            variant="outline"
            className="border-border"
            onClick={handleDismiss}
          >
            Maybe later
          </Button>
        </div>
      </div>

      <LeaveReviewDialog
        open={dialogOpen}
        onOpenChange={setDialogOpen}
        assetId={assetId}
        assetTitle={title}
        onSubmitted={handleReviewSubmitted}
      />
    </>
  )
}
