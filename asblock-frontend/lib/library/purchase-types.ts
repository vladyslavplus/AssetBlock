export interface PurchaseLibraryItem {
  id: string
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
}

export interface PagedPurchaseLibraryDto {
  items: PurchaseLibraryItem[]
  totalCount: number
  page: number
  pageSize: number
}
