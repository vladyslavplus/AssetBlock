'use client'

import Link from 'next/link'
import { useRouter } from 'next/navigation'
import { useMutation } from '@tanstack/react-query'
import { Download, Lock, Loader2, Zap } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/components/auth/auth-context'
import { isEmailVerified } from '@/components/auth/email-verification-notice'
import { formatUsdWhole } from '@/lib/format-currency'
import type { CheckoutAttributionInput } from '@/lib/analytics/telemetry-source'
import { CheckoutRequestError, postCreateBundleCheckoutSession } from '@/lib/payments/checkout-api'
import { writePendingCheckoutContext } from '@/lib/reviews/review-constants'
import { routes } from '@/lib/routes'
import type { BundleItem } from '@/lib/bundles/bundle-types'

interface BundlePurchaseCardProps {
  bundleId: string
  sellerId: string
  title: string
  price: number
  listPriceTotal: number
  savingsAmount: number
  savingsPercent: number
  isAvailable: boolean
  items: BundleItem[]
  checkoutConfigured: boolean
  returnPath: string
  checkoutAttribution?: CheckoutAttributionInput
}

export function BundlePurchaseCard({
  bundleId,
  sellerId,
  title,
  price,
  listPriceTotal,
  savingsAmount,
  savingsPercent,
  isAvailable,
  items: _items,
  checkoutConfigured,
  returnPath,
  checkoutAttribution,
}: BundlePurchaseCardProps) {
  const router = useRouter()
  const { user, status } = useAuth()
  const isOwner = Boolean(user && user.id === sellerId)
  const verified = isEmailVerified(user)
  const loginHref = routes.login(returnPath)

  const checkoutMutation = useMutation({
    mutationFn: () => postCreateBundleCheckoutSession(bundleId, checkoutAttribution),
    onSuccess: (data) => {
      writePendingCheckoutContext({
        checkoutIntentId: data.checkoutIntentId,
        kind: 'bundle',
        bundleId,
      })
      window.location.assign(data.checkoutUrl)
    },
    onError: (err: unknown) => {
      if (err instanceof CheckoutRequestError) {
        if (err.status === 401) {
          toast.error('Session expired. Sign in again.')
          router.push(loginHref)
          return
        }
        if (err.status === 409) {
          if (err.code === 'ERR_BUNDLE_CONTAINS_OWNED_ASSET') {
            toast.error('You already own an item in this bundle. Bundle purchase unavailable.')
            return
          }
          toast.error(err.message)
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

  const savingsLabel =
    savingsAmount > 0
      ? `Save ${formatUsdWhole(savingsAmount)} (${Math.round(savingsPercent)}%)`
      : null

  return (
    <div className="flex min-w-0 flex-col gap-4 rounded-lg border border-border bg-card-elevated p-5">
      <div className="flex min-w-0 flex-col gap-1">
        <h3 className="line-clamp-2 break-words text-balance text-sm font-semibold text-foreground">
          {title}
        </h3>
        <p className="text-2xl font-semibold font-mono text-foreground">{formatUsdWhole(price)}</p>
        <p className="text-xs text-muted-foreground">
          List total{' '}
          <span className="line-through font-mono">{formatUsdWhole(listPriceTotal)}</span>
        </p>
        {savingsLabel ? <p className="text-xs font-medium text-accent">{savingsLabel}</p> : null}
      </div>

      {!isAvailable ? (
        <p className="text-sm text-muted-foreground rounded-md border border-border/60 bg-secondary/30 px-3 py-2">
          This bundle cannot be purchased (archived or unavailable).
        </p>
      ) : isOwner ? (
        <p className="text-sm text-muted-foreground rounded-md border border-border/60 bg-secondary/30 px-3 py-2">
          This is your bundle. Buyers will use checkout here.
        </p>
      ) : status === 'anonymous' ? (
        <Button
          type="button"
          asChild
          className="bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium w-full h-10"
        >
          <Link href={loginHref}>Sign in to purchase</Link>
        </Button>
      ) : !verified ? (
        <Button
          type="button"
          asChild
          className="bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium w-full h-10"
        >
          <Link href="/account">Verify email to purchase</Link>
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
            'Buy bundle'
          )}
        </Button>
      )}

      {!checkoutConfigured && !isOwner && status !== 'anonymous' && isAvailable ? (
        <p className="text-xs text-muted-foreground">
          Payments are not configured on the server. Set Stripe keys and default redirect URLs in
          the API.
        </p>
      ) : null}

      <div className="flex flex-col gap-2 pt-2 border-t border-border/50">
        <div className="flex items-center gap-2 text-xs">
          <Lock className="size-4 text-accent shrink-0" aria-hidden />
          <span className="text-muted-foreground leading-none">Secure checkout</span>
        </div>
        <div className="flex items-center gap-2 text-xs">
          <Zap className="size-4 text-accent shrink-0" aria-hidden />
          <span className="text-muted-foreground leading-none">All assets unlocked at once</span>
        </div>
        <div className="flex items-center gap-2 text-xs">
          <Download className="size-4 text-accent shrink-0" aria-hidden />
          <span className="text-muted-foreground leading-none">Lifetime access</span>
        </div>
      </div>
    </div>
  )
}
