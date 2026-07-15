export interface AssetReview {
  id: string
  authorUsername: string
  rating: number
  body: string
  createdAt: string
  verifiedPurchase?: boolean
}
