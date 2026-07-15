import type { AssetListItemApi, PagedResultDto } from '@/lib/catalog/assets-api'
import { fetchMyListings } from '@/lib/seller/seller-api'

export const sellerKeys = {
  all: ['seller'] as const,
  listings: () => [...sellerKeys.all, 'listings'] as const,
}

export async function fetchSellerListingsQuery(): Promise<PagedResultDto<AssetListItemApi>> {
  return fetchMyListings()
}
