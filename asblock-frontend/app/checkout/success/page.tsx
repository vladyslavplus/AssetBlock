import { CheckCircle2 } from 'lucide-react'
import Link from 'next/link'
import { Button } from '@/components/ui/button'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { SiteHeader } from '@/components/site-header'
import { SiteFooter } from '@/components/site-footer'
import { InvalidateLibraryAfterCheckout } from '@/components/checkout/invalidate-library-after-checkout'
import { PostCheckoutReviewBanner } from '@/components/reviews/post-checkout-review-banner'

export const metadata = {
  title: 'Checkout - AssetBlock',
  description: 'Confirming your AssetBlock checkout.',
}

export default function CheckoutSuccessPage() {
  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />

      <SiteMain>
        <InvalidateLibraryAfterCheckout />
        <SitePageContainer variant="receipt" padding="none">
          <div className="bg-card-elevated border border-border rounded-xl p-6 sm:p-8 space-y-6">
            <div className="flex justify-center">
              <CheckCircle2 className="w-12 h-12 text-primary" />
            </div>

            <h1 className="text-2xl font-bold text-center text-foreground">Checkout</h1>

            <p className="text-sm text-muted-foreground text-center leading-relaxed">
              If you completed payment in Stripe, we are confirming it with the payment provider.
              Your library unlocks after confirmation — not from this page alone.
            </p>

            <PostCheckoutReviewBanner />

            <div className="flex flex-col gap-3 pt-2">
              <Button
                asChild
                className="bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium w-full h-9"
              >
                <Link href="/library">View my purchases</Link>
              </Button>
              <Button
                asChild
                variant="outline"
                className="border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground transition-smooth font-medium w-full h-9"
              >
                <Link href="/assets">Back to catalog</Link>
              </Button>
            </div>

            <p className="text-xs text-muted-foreground text-center">
              You can close this tab if you opened checkout in a new window.
            </p>
          </div>
        </SitePageContainer>
      </SiteMain>

      <SiteFooter />
    </div>
  )
}
