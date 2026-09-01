'use client'

import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import Link from 'next/link'
import { Loader2, Package, Star } from 'lucide-react'

import { LeaveReviewDialog } from '@/components/reviews/leave-review-dialog'
import { Button } from '@/components/ui/button'
import { checkoutStatusQueryOptions } from '@/lib/payments/checkout-query'
import { libraryKeys } from '@/lib/library/library-query'
import { notificationsKeys } from '@/lib/notifications/notifications-query'
import { invalidateQueriesInBackground, runQueryInBackground } from '@/lib/query/query-refresh'
import {
  clearPendingCheckoutContext,
  readPendingCheckoutContext,
  type PendingCheckoutContext,
} from '@/lib/reviews/review-constants'

const POLL_MS = 2000
const POLL_TIMEOUT_MS = 2 * 60 * 1000

export function PostCheckoutReviewBanner() {
  const queryClient = useQueryClient()
  const libraryInvalidatedRef = useRef(false)
  const [storageReady, setStorageReady] = useState(false)
  const [context, setContext] = useState<PendingCheckoutContext | null>(null)
  const [dismissed, setDismissed] = useState(false)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [pollDeadline, setPollDeadline] = useState(() => Date.now() + POLL_TIMEOUT_MS)
  const [pollTimedOut, setPollTimedOut] = useState(false)

  useEffect(() => {
    const frame = requestAnimationFrame(() => {
      setStorageReady(true)
      setContext(readPendingCheckoutContext())
    })
    return () => cancelAnimationFrame(frame)
  }, [])

  const statusQuery = useQuery({
    ...checkoutStatusQueryOptions(context?.checkoutIntentId),
    refetchInterval: (q) => (q.state.data?.status === 'pending' && !pollTimedOut ? POLL_MS : false),
    retry: 1,
  })

  useEffect(() => {
    if (statusQuery.data?.status !== 'pending' || pollTimedOut) return
    const timeout = window.setTimeout(
      () => setPollTimedOut(true),
      Math.max(0, pollDeadline - Date.now()),
    )
    return () => window.clearTimeout(timeout)
  }, [pollDeadline, pollTimedOut, statusQuery.data?.status])

  useEffect(() => {
    if (statusQuery.data?.status !== 'completed' || libraryInvalidatedRef.current) return
    libraryInvalidatedRef.current = true
    invalidateQueriesInBackground(queryClient, { queryKey: libraryKeys.purchases() })
    invalidateQueriesInBackground(queryClient, { queryKey: notificationsKeys.all })
  }, [statusQuery.data?.status, queryClient])

  const handleDismiss = () => {
    clearPendingCheckoutContext()
    setDismissed(true)
  }

  const handleReviewSubmitted = () => {
    clearPendingCheckoutContext()
    setDismissed(true)
  }

  const handleCheckAgain = () => {
    setPollDeadline(Date.now() + POLL_TIMEOUT_MS)
    setPollTimedOut(false)
    runQueryInBackground(statusQuery.refetch())
  }

  if (dismissed || !storageReady || !context) {
    return null
  }

  if (statusQuery.data?.status === 'pending' && pollTimedOut) {
    return (
      <div
        className="mb-6 rounded-lg border border-border bg-secondary/20 px-4 py-4 space-y-3"
        role="region"
        aria-label="Payment confirmation delayed"
      >
        <p className="text-sm font-medium text-foreground">Payment confirmation is taking longer</p>
        <p className="text-xs text-muted-foreground leading-relaxed">
          Automatic checks paused after two minutes. If you were charged, your library will update
          when the payment webhook arrives.
        </p>
        <div className="flex flex-wrap gap-2">
          <Button type="button" size="sm" variant="outline" onClick={handleCheckAgain}>
            Check again
          </Button>
          <Button type="button" size="sm" variant="ghost" onClick={handleDismiss}>
            Dismiss
          </Button>
        </div>
      </div>
    )
  }

  if (statusQuery.isLoading || statusQuery.data?.status === 'pending') {
    return (
      <div
        className="mb-6 rounded-lg border border-border bg-secondary/20 px-4 py-4 space-y-2"
        role="status"
        aria-live="polite"
      >
        <div className="flex gap-2 items-start">
          <Loader2 className="size-5 shrink-0 text-muted-foreground animate-spin" aria-hidden />
          <div className="min-w-0 space-y-1">
            <p className="text-sm font-medium text-foreground">Processing payment</p>
            <p className="text-xs text-muted-foreground leading-relaxed">
              Waiting for payment confirmation. This usually takes a few seconds.
            </p>
          </div>
        </div>
      </div>
    )
  }

  if (statusQuery.isError || statusQuery.data?.status === 'cancelled') {
    return (
      <div
        className="mb-6 rounded-lg border border-border bg-secondary/20 px-4 py-4 space-y-3"
        role="region"
        aria-label="Checkout not completed"
      >
        <p className="text-sm font-medium text-foreground">Payment not completed</p>
        <p className="text-xs text-muted-foreground leading-relaxed">
          We could not confirm this checkout. If you were charged, your library will update shortly
          after the webhook arrives — otherwise try checkout again.
        </p>
        <Button
          type="button"
          size="sm"
          variant="outline"
          className="border-border"
          onClick={handleDismiss}
        >
          Dismiss
        </Button>
      </div>
    )
  }

  const title = statusQuery.data?.productTitle?.trim() || 'Your purchase'

  if (context.kind === 'bundle') {
    return (
      <div
        className="mb-6 rounded-lg border border-border bg-secondary/20 px-4 py-4 space-y-3"
        role="region"
        aria-label="Bundle purchase complete"
      >
        <div className="flex gap-2">
          <Package className="size-5 shrink-0 text-accent" aria-hidden />
          <div className="min-w-0 space-y-1">
            <p className="text-sm font-medium text-foreground">Bundle unlocked</p>
            <p className="text-xs text-muted-foreground leading-relaxed">
              <span className="font-medium text-foreground break-words">{title}</span> is in your
              library. You can leave reviews on individual assets from{' '}
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
            asChild
            className="bg-primary text-primary-foreground hover:bg-[#6D28D9] font-medium"
          >
            <Link href="/library" onClick={handleDismiss}>
              Open library
            </Link>
          </Button>
          <Button
            type="button"
            size="sm"
            variant="outline"
            className="border-border"
            onClick={handleDismiss}
          >
            Dismiss
          </Button>
        </div>
      </div>
    )
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
        assetId={context.assetId}
        assetTitle={title}
        onSubmitted={handleReviewSubmitted}
      />
    </>
  )
}
