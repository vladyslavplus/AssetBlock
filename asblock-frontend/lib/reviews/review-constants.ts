/** sessionStorage key set before Stripe redirect; cleared after review prompt is done or skipped. */
export const PENDING_REVIEW_ASSET_ID_KEY = 'assetblock_pending_review_asset_id'

export const MAX_REVIEW_DAYS_AFTER_PURCHASE = 14

const MS_PER_DAY = 24 * 60 * 60 * 1000

/** Rolling N-day window after purchase. */
export function isWithinReviewWindowAfterPurchase(purchasedAtIso: string): boolean {
  const t = Date.parse(purchasedAtIso)
  if (Number.isNaN(t)) return false
  return Date.now() - t <= MAX_REVIEW_DAYS_AFTER_PURCHASE * MS_PER_DAY
}
