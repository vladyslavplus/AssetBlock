import type { CollectionStatus } from '@/lib/collections/collection-schemas'

export type { CollectionStatus }

export interface CollectionListItem {
  id: string
  title: string
  description: string | null
  status: CollectionStatus
  publishedAt: string | null
  createdAt: string
  sellerId: string
  sellerUsername: string
  itemCount: number
  coverAssetId: string | null
  coverAssetTitle: string | null
}

export interface CollectionItem {
  assetId: string
  title: string
  price: number
  position: number
  isAvailable: boolean
  unavailableReason: string | null
}

export interface CollectionDetail {
  id: string
  title: string
  description: string | null
  status: CollectionStatus
  publishedAt: string | null
  archivedAt: string | null
  createdAt: string
  updatedAt: string | null
  sellerId: string
  sellerUsername: string
  items: CollectionItem[]
}

export interface CreateCollectionResponse {
  id: string
}

export interface PagedCollections {
  items: CollectionListItem[]
  totalCount: number
  page: number
  pageSize: number
}
