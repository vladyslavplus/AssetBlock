import {
  fetchMyCollection,
  fetchMyCollections,
  fetchPublicCollection,
  fetchPublicCollectionsPage,
  type CollectionListFilters,
} from '@/lib/collections/collections-api'
import type { CollectionDetail, PagedCollections } from '@/lib/collections/collection-types'

export const collectionKeys = {
  all: ['collections'] as const,
  publicList: (filters: CollectionListFilters) =>
    [...collectionKeys.all, 'public', 'list', filters] as const,
  publicDetail: (id: string) => [...collectionKeys.all, 'public', 'detail', id] as const,
  sellerList: () => [...collectionKeys.all, 'seller', 'list'] as const,
  sellerDetail: (id: string) => [...collectionKeys.all, 'seller', 'detail', id] as const,
}

export async function fetchCollectionsListQuery(
  filters: CollectionListFilters,
): Promise<PagedCollections & { totalPages: number }> {
  return fetchPublicCollectionsPage(filters)
}

export async function fetchCollectionDetailQuery(id: string): Promise<CollectionDetail> {
  return fetchPublicCollection(id)
}

export async function fetchSellerCollectionsQuery({
  signal,
}: { signal?: AbortSignal } = {}): Promise<PagedCollections> {
  return fetchMyCollections(signal)
}

export async function fetchSellerCollectionQuery(
  id: string,
  signal?: AbortSignal,
): Promise<CollectionDetail> {
  return fetchMyCollection(id, signal)
}
