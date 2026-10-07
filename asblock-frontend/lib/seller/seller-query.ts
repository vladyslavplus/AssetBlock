import type { PagedResultDto } from '@/lib/catalog/assets-api'
import { fetchMyListings, fetchSellerAssetDetail } from '@/lib/seller/seller-api'
import {
  fetchSellerDraft,
  fetchSellerDeclaration,
  fetchSellerVersionReview,
} from '@/lib/seller/seller-draft-api'
import type { SellerAssetDetail, SellerAssetListItem } from '@/lib/seller/seller-asset-schemas'
import type {
  SellerDraftSnapshot,
  SellerDeclarationSnapshot,
  SellerVersionReview,
} from '@/lib/seller/seller-draft-schemas'

export const sellerKeys = {
  all: ['seller'] as const,
  listings: () => [...sellerKeys.all, 'listings'] as const,
  detail: (assetId: string) => [...sellerKeys.all, 'detail', assetId] as const,
  versions: (assetId: string) => [...sellerKeys.all, 'versions', assetId] as const,
  draft: (assetId: string) => [...sellerKeys.all, 'draft', assetId] as const,
  declaration: (assetId: string, versionId: string | null) =>
    [...sellerKeys.all, 'declaration', assetId, versionId ?? 'pre-upload'] as const,
  versionReview: (assetId: string, versionId: string) =>
    [...sellerKeys.all, 'version-review', assetId, versionId] as const,
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

export async function fetchSellerDraftQuery({
  assetId,
  signal,
}: {
  assetId: string
  signal?: AbortSignal
}): Promise<SellerDraftSnapshot> {
  return fetchSellerDraft(assetId, signal)
}

export async function fetchSellerDeclarationQuery({
  assetId,
  versionId,
  signal,
}: {
  assetId: string
  versionId: string | null
  signal?: AbortSignal
}): Promise<SellerDeclarationSnapshot> {
  return fetchSellerDeclaration(assetId, versionId, signal)
}

export async function fetchSellerVersionReviewQuery({
  assetId,
  versionId,
  signal,
}: {
  assetId: string
  versionId: string
  signal?: AbortSignal
}): Promise<SellerVersionReview> {
  return fetchSellerVersionReview(assetId, versionId, signal)
}
