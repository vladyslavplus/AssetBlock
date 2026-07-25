import {
  fetchMyBundle,
  fetchMyBundles,
  fetchPublicBundle,
  fetchPublicBundlesPage,
  type BundleListFilters,
} from '@/lib/bundles/bundles-api'
import type { BundleDetail, PagedBundles } from '@/lib/bundles/bundle-types'

export const bundleKeys = {
  all: ['bundles'] as const,
  publicList: (filters: BundleListFilters) =>
    [...bundleKeys.all, 'public', 'list', filters] as const,
  publicDetail: (id: string) => [...bundleKeys.all, 'public', 'detail', id] as const,
  sellerList: () => [...bundleKeys.all, 'seller', 'list'] as const,
  sellerDetail: (id: string) => [...bundleKeys.all, 'seller', 'detail', id] as const,
}

export async function fetchBundlesListQuery(
  filters: BundleListFilters,
): Promise<PagedBundles & { totalPages: number }> {
  return fetchPublicBundlesPage(filters)
}

export async function fetchBundleDetailQuery(id: string): Promise<BundleDetail> {
  return fetchPublicBundle(id)
}

export async function fetchSellerBundlesQuery(): Promise<PagedBundles> {
  return fetchMyBundles()
}

export async function fetchSellerBundleQuery(id: string): Promise<BundleDetail> {
  return fetchMyBundle(id)
}
