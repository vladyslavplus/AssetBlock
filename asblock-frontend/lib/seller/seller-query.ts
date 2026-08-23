import type { AssetListItemApi, PagedResultDto } from '@/lib/catalog/assets-api'
import { fetchMyListings } from '@/lib/seller/seller-api'

export const sellerKeys = {
  all: ['seller'] as const,
  listings: () => [...sellerKeys.all, 'listings'] as const,
  versions: (assetId: string) => [...sellerKeys.all, 'versions', assetId] as const,
}

export async function fetchSellerListingsQuery({
  signal,
}: {
  signal?: AbortSignal
} = {}): Promise<PagedResultDto<AssetListItemApi>> {
  return fetchMyListings(signal)
}
