import type { PagedResultDto } from '@/lib/catalog/assets-api'
import { fetchMyListings, fetchSellerAssetDetail } from '@/lib/seller/seller-api'
import type { SellerAssetDetail, SellerAssetListItem } from '@/lib/seller/seller-asset-schemas'

export const sellerKeys = {
  all: ['seller'] as const,
  listings: () => [...sellerKeys.all, 'listings'] as const,
  detail: (assetId: string) => [...sellerKeys.all, 'detail', assetId] as const,
  versions: (assetId: string) => [...sellerKeys.all, 'versions', assetId] as const,
}

export async function fetchSellerListingsQuery({
  signal,
}: {
  signal?: AbortSignal
} = {}): Promise<PagedResultDto<SellerAssetListItem>> {
  return fetchMyListings(signal)
}

export async function fetchSellerAssetDetailQuery({
  assetId,
  signal,
}: {
  assetId: string
  signal?: AbortSignal
}): Promise<SellerAssetDetail> {
  return fetchSellerAssetDetail(assetId, signal)
}
