export interface BundleListItem {
  id: string
  revisionId: string
  revisionNumber: number
  title: string
  description: string | null
  price: number
  listPriceTotal: number
  savingsAmount: number
  savingsPercent: number
  currency: string
  itemCount: number
  sellerId: string
  sellerUsername: string
  createdAt: string
  isArchived: boolean
  isAvailable: boolean
}

export interface BundleItem {
  assetId: string | null
  title: string
  listPrice: number
  position: number
  isAvailable: boolean
  unavailableReason: string | null
  currentVersionNumber: number | null
  licenseCode: string | null
  licenseDisplayName: string | null
}

export interface BundleDetail {
  id: string
  revisionId: string
  revisionNumber: number
  title: string
  description: string | null
  price: number
  listPriceTotal: number
  savingsAmount: number
  savingsPercent: number
  currency: string
  sellerId: string
  sellerUsername: string
  createdAt: string
  updatedAt: string | null
  archivedAt: string | null
  isArchived: boolean
  isAvailable: boolean
  items: BundleItem[]
}

export interface CreateBundleResponse {
  id: string
  revisionId: string
  revisionNumber: number
}

export type ReviseBundleResponse = CreateBundleResponse

export interface PagedBundles {
  items: BundleListItem[]
  totalCount: number
  page: number
  pageSize: number
}
