import { z } from 'zod'

/** sessionStorage key set before Stripe redirect; cleared after review prompt is done or skipped. */
export const PENDING_CHECKOUT_CONTEXT_KEY = 'assetblock_pending_checkout_context'

const pendingCheckoutContextSchema = z.discriminatedUnion('kind', [
  z.object({
    checkoutIntentId: z.string().uuid(),
    kind: z.literal('asset'),
    assetId: z.string().uuid(),
  }),
  z.object({
    checkoutIntentId: z.string().uuid(),
    kind: z.literal('bundle'),
    bundleId: z.string().uuid(),
  }),
])

export type PendingCheckoutContext = z.infer<typeof pendingCheckoutContextSchema>

export const MAX_REVIEW_DAYS_AFTER_PURCHASE = 14

const MS_PER_DAY = 24 * 60 * 60 * 1000

/** Rolling N-day window after purchase. */
export function isWithinReviewWindowAfterPurchase(purchasedAtIso: string): boolean {
  const t = Date.parse(purchasedAtIso)
  if (Number.isNaN(t)) return false
  return Date.now() - t <= MAX_REVIEW_DAYS_AFTER_PURCHASE * MS_PER_DAY
}

export function writePendingCheckoutContext(context: PendingCheckoutContext): void {
  try {
    sessionStorage.setItem(PENDING_CHECKOUT_CONTEXT_KEY, JSON.stringify(context))
  } catch {
    // Private mode / storage blocked — success page prompt may be unavailable.
  }
}

export function readPendingCheckoutContext(): PendingCheckoutContext | null {
  try {
    const raw = sessionStorage.getItem(PENDING_CHECKOUT_CONTEXT_KEY)?.trim()
    if (!raw) return null
    const parsed = pendingCheckoutContextSchema.safeParse(JSON.parse(raw))
    return parsed.success ? parsed.data : null
  } catch {
    /* ignore */
  }
  return null
}

export function clearPendingCheckoutContext(): void {
  try {
    sessionStorage.removeItem(PENDING_CHECKOUT_CONTEXT_KEY)
  } catch {
    /* ignore */
  }
}
