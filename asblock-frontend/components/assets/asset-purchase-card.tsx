'use client'

import Link from 'next/link'
import { useRouter } from 'next/navigation'
import { useMutation } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/components/auth/auth-context'
import { formatUsdWhole } from '@/lib/format-currency'
import { CheckoutRequestError, postCreateCheckoutSession } from '@/lib/payments/checkout-api'
import { PENDING_REVIEW_ASSET_ID_KEY } from '@/lib/reviews/review-constants'
import { Download, Lock, Loader2, Zap } from 'lucide-react'
import { toast } from 'sonner'

interface AssetPurchaseCardProps {
  assetId: string
  authorId: string
  title: string
  price: number
  checkoutConfigured: boolean
  returnPath: string
}

export function AssetPurchaseCard({
  assetId,
  authorId,
  title,
  price,
  checkoutConfigured,
  returnPath,
}: AssetPurchaseCardProps) {
  const router = useRouter()
  const { user, status } = useAuth()

  const isOwner = Boolean(user && user.id === authorId)
  const loginHref = `/login?returnUrl=${encodeURIComponent(returnPath)}`

  const checkoutMutation = useMutation({
    mutationFn: () => postCreateCheckoutSession(assetId),
    onSuccess: (data) => {
      try {
        sessionStorage.setItem(PENDING_REVIEW_ASSET_ID_KEY, assetId)
      } catch {
        // Private mode / storage blocked — success page prompt may be unavailable; library still works.
      }
      window.location.assign(data.checkoutUrl)
    },
    onError: (err: unknown) => {
      if (err instanceof CheckoutRequestError) {
        if (err.status === 401) {
          toast.error('Session expired. Sign in again.')
          router.push(loginHref)
          return
        }
        toast.error(err.message)
        return
      }
      toast.error('Could not start checkout. Try again.')
    },
  })

  const onBuyClick = () => {
    if (status === 'loading' || checkoutMutation.isPending) return
    if (status === 'anonymous' || !user) {
      router.push(loginHref)
      return
    }
    checkoutMutation.mutate()
  }

  return (
    <div className="flex min-w-0 flex-col gap-4 rounded-lg border border-border bg-card-elevated p-5">
      <div className="flex min-w-0 flex-col gap-1">
        <h3 className="line-clamp-2 break-words text-balance text-sm font-semibold text-foreground">
          {title}
        </h3>
        <p className="text-2xl font-semibold font-mono text-foreground">{formatUsdWhole(price)}</p>
      </div>

      {isOwner ? (
        <p className="text-sm text-muted-foreground rounded-md border border-border/60 bg-secondary/30 px-3 py-2">
          This is your listing. Buyers will use checkout here once it is published.
        </p>
      ) : status === 'anonymous' ? (
        <Button
          type="button"
          asChild
          className="bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium w-full h-10"
        >
          <Link href={loginHref}>Sign in to purchase</Link>
        </Button>
      ) : !checkoutConfigured ? (
        <Button type="button" disabled className="w-full h-10 font-medium">
          Checkout unavailable
        </Button>
      ) : (
        <Button
          type="button"
          disabled={status === 'loading' || checkoutMutation.isPending}
          className="bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium w-full h-10"
          onClick={onBuyClick}
        >
          {checkoutMutation.isPending || status === 'loading' ? (
            <>
              <Loader2 className="mr-2 size-4 animate-spin" aria-hidden />
              Redirecting…
            </>
          ) : (
            'Buy now'
          )}
        </Button>
      )}

      {!checkoutConfigured && !isOwner && status !== 'anonymous' && (
        <p className="text-xs text-muted-foreground">
          Payments are not configured on the server. Set Stripe keys and default redirect URLs in
          the API.
        </p>
      )}

      <div className="flex flex-col gap-2 pt-2 border-t border-border/50">
        <div className="flex items-center gap-2 text-xs">
          <Lock className="size-4 text-accent shrink-0" aria-hidden />
          <span className="text-muted-foreground leading-none">Secure checkout</span>
        </div>
        <div className="flex items-center gap-2 text-xs">
          <Zap className="size-4 text-accent shrink-0" aria-hidden />
          <span className="text-muted-foreground leading-none">Instant delivery</span>
        </div>
        <div className="flex items-center gap-2 text-xs">
          <Download className="size-4 text-accent shrink-0" aria-hidden />
          <span className="text-muted-foreground leading-none">Lifetime access</span>
        </div>
      </div>
    </div>
  )
}
