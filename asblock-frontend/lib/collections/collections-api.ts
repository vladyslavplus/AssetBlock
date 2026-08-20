import { apiFetch } from '@/lib/http/api-client'
import { parseApiErrorBody } from '@/lib/http/api-errors'
import { fetchBffJson } from '@/lib/http/bff-json'
import { z } from 'zod'
import type { CollectionDetail, PagedCollections } from '@/lib/collections/collection-types'
import {
  collectionDetailResponseSchema,
  createCollectionResponseSchema,
  pagedCollectionsResponseSchema,
} from '@/lib/collections/collection-schemas'
import type {
  AddCollectionItemBody,
  CreateCollectionBody,
  ReorderCollectionItemsBody,
  UpdateCollectionBody,
} from '@/lib/collections/collection-schemas'

export const COLLECTIONS_PAGE_SIZE = 12

export interface CollectionListFilters {
  page: number
  search?: string
  sortBy?: string
  sortDirection?: 'ASC' | 'DESC'
}

export async function fetchPublicCollectionsPage(
  filters: CollectionListFilters,
  signal?: AbortSignal,
): Promise<PagedCollections & { totalPages: number }> {
  const params = new URLSearchParams({
    page: String(filters.page),
    pageSize: String(COLLECTIONS_PAGE_SIZE),
    sortBy: filters.sortBy ?? 'PublishedAt',
    sortDirection: filters.sortDirection ?? 'DESC',
  })
  const q = filters.search?.trim()
  if (q) params.set('search', q)

  const raw = await apiFetch<unknown>({
    path: `api/collections?${params.toString()}`,
    method: 'GET',
    signal,
  })
  const data = pagedCollectionsResponseSchema.parse(raw)
  return {
    ...data,
    totalPages: Math.ceil(data.totalCount / data.pageSize),
  }
}

export async function fetchPublicCollection(
  id: string,
  signal?: AbortSignal,
): Promise<CollectionDetail> {
  const raw = await apiFetch<unknown>({
    path: `api/collections/${encodeURIComponent(id)}`,
    method: 'GET',
    signal,
  })
  return collectionDetailResponseSchema.parse(raw)
}

export type SellerMutationResult =
  | { ok: true }
  | { ok: false; message: string; fieldErrors?: Record<string, string> }

export type SellerCreateCollectionResult =
  | { ok: true; id: string }
  | { ok: false; message: string; fieldErrors?: Record<string, string> }

export async function fetchMyCollections(signal?: AbortSignal): Promise<PagedCollections> {
  const params = new URLSearchParams({
    page: '1',
    pageSize: '50',
    sortBy: 'UpdatedAt',
    sortDirection: 'DESC',
  })
  const result = await fetchBffJson(
    `/api/seller/collections?${params}`,
    pagedCollectionsResponseSchema,
    { method: 'GET', signal },
  )
  if (!result.ok) {
    if (result.status === 401) throw new Error('SIGN_IN_REQUIRED')
    throw new Error(result.message)
  }
  return result.data
}

export async function fetchMyCollection(
  id: string,
  signal?: AbortSignal,
): Promise<CollectionDetail> {
  const result = await fetchBffJson(
    `/api/seller/collections/${encodeURIComponent(id)}`,
    collectionDetailResponseSchema,
    { method: 'GET', signal },
  )
  if (!result.ok) {
    if (result.status === 401) throw new Error('SIGN_IN_REQUIRED')
    throw new Error(result.message)
  }
  return result.data
}

export async function createSellerCollection(
  body: CreateCollectionBody,
): Promise<SellerCreateCollectionResult> {
  const result = await fetchBffJson('/api/seller/collections', createCollectionResponseSchema, {
    method: 'POST',
    body: JSON.stringify({
      title: body.title,
      description: body.description?.trim() ? body.description.trim() : null,
    }),
  })
  if (!result.ok) {
    const p = parseApiErrorBody(result.body)
    const fe = p?.fieldErrors
    return {
      ok: false,
      message: result.message,
      ...(fe && Object.keys(fe).length > 0 ? { fieldErrors: fe } : {}),
    }
  }
  return { ok: true, id: result.data.id }
}

export async function updateSellerCollection(
  id: string,
  body: UpdateCollectionBody,
): Promise<SellerMutationResult> {
  const result = await fetchBffJson(
    `/api/seller/collections/${encodeURIComponent(id)}`,
    z.unknown(),
    {
      method: 'PATCH',
      body: JSON.stringify({
        title: body.title,
        description: body.description?.trim() ? body.description.trim() : null,
      }),
    },
  )
  if (!result.ok) {
    const p = parseApiErrorBody(result.body)
    const fe = p?.fieldErrors
    return {
      ok: false,
      message: result.message,
      ...(fe && Object.keys(fe).length > 0 ? { fieldErrors: fe } : {}),
    }
  }
  return { ok: true }
}

export async function addSellerCollectionItem(
  id: string,
  body: AddCollectionItemBody,
): Promise<SellerMutationResult> {
  const result = await fetchBffJson(
    `/api/seller/collections/${encodeURIComponent(id)}/items`,
    z.unknown(),
    { method: 'POST', body: JSON.stringify(body) },
  )
  if (!result.ok) return { ok: false, message: result.message }
  return { ok: true }
}

export async function removeSellerCollectionItem(
  id: string,
  assetId: string,
): Promise<SellerMutationResult> {
  const result = await fetchBffJson(
    `/api/seller/collections/${encodeURIComponent(id)}/items/${encodeURIComponent(assetId)}`,
    z.unknown(),
    { method: 'DELETE' },
  )
  if (!result.ok) return { ok: false, message: result.message }
  return { ok: true }
}

export async function reorderSellerCollectionItems(
  id: string,
  body: ReorderCollectionItemsBody,
): Promise<SellerMutationResult> {
  const result = await fetchBffJson(
    `/api/seller/collections/${encodeURIComponent(id)}/items/order`,
    z.unknown(),
    { method: 'PUT', body: JSON.stringify(body) },
  )
  if (!result.ok) return { ok: false, message: result.message }
  return { ok: true }
}

export async function publishSellerCollection(id: string): Promise<SellerMutationResult> {
  const result = await fetchBffJson(
    `/api/seller/collections/${encodeURIComponent(id)}/publish`,
    z.unknown(),
    { method: 'POST' },
  )
  if (!result.ok) return { ok: false, message: result.message }
  return { ok: true }
}

export async function archiveSellerCollection(id: string): Promise<SellerMutationResult> {
  const result = await fetchBffJson(
    `/api/seller/collections/${encodeURIComponent(id)}/archive`,
    z.unknown(),
    { method: 'POST' },
  )
  if (!result.ok) return { ok: false, message: result.message }
  return { ok: true }
}

export async function restoreSellerCollection(id: string): Promise<SellerMutationResult> {
  const result = await fetchBffJson(
    `/api/seller/collections/${encodeURIComponent(id)}/restore`,
    z.unknown(),
    { method: 'POST' },
  )
  if (!result.ok) return { ok: false, message: result.message }
  return { ok: true }
}
