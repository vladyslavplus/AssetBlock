import { redirect } from 'next/navigation'

/** Backend StripeOptions default uses /payment/cancel; app UI lives under /checkout. */
export default function PaymentCancelAliasPage() {
  redirect('/checkout/cancel')
}
