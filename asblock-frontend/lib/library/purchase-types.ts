export type PurchaseSource = 'ASSET' | 'BUNDLE'

export interface PurchaseLibraryItem {
  id: string
  orderId: string
  assetId: string
  assetTitle: string
  price: number
  purchasedAt: string
  authorUsername: string
  hasUserReviewed: boolean
  purchasedVersionNumber: number
  purchasedVersionId: string
  latestEntitledVersionNumber: number
  latestEntitledVersionId: string
  hasUpdate: boolean
  pricePaid: number
  currency: string
  source: PurchaseSource
  bundleId: string | null
  bundleTitle: string | null
}

export interface PagedPurchaseLibraryDto {
  items: PurchaseLibraryItem[]
  totalCount: number
  page: number
  pageSize: number
}

/** Normalize backend string enum to ASSET | BUNDLE. */
export function normalizePurchaseSource(raw: unknown): PurchaseSource {
  if (raw === 'BUNDLE') return 'BUNDLE'
  return 'ASSET'
}
