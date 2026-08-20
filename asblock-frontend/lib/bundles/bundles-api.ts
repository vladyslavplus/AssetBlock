import { apiFetch } from '@/lib/http/api-client'
import { parseApiErrorBody } from '@/lib/http/api-errors'
import { fetchBffJson } from '@/lib/http/bff-json'
import { z } from 'zod'
import type { BundleDetail, CreateBundleResponse, PagedBundles } from '@/lib/bundles/bundle-types'
import {
  bundleDetailResponseSchema,
  createBundleResponseSchema,
  pagedBundlesResponseSchema,
  type CreateBundleBody,
  type ReviseBundleBody,
} from '@/lib/bundles/bundle-schemas'

export const BUNDLES_PAGE_SIZE = 12

export interface BundleListFilters {
  page: number
  search?: string
  sortBy?: string
  sortDirection?: 'ASC' | 'DESC'
}

export async function fetchPublicBundlesPage(
  filters: BundleListFilters,
  signal?: AbortSignal,
): Promise<PagedBundles & { totalPages: number }> {
  const params = new URLSearchParams({
    page: String(filters.page),
    pageSize: String(BUNDLES_PAGE_SIZE),
    sortBy: filters.sortBy ?? 'CreatedAt',
    sortDirection: filters.sortDirection ?? 'DESC',
  })
  const q = filters.search?.trim()
  if (q) params.set('search', q)

  const raw = await apiFetch<unknown>({
    path: `api/bundles?${params.toString()}`,
    method: 'GET',
    signal,
  })
  const data = pagedBundlesResponseSchema.parse(raw)
  return {
    ...data,
    totalPages: Math.ceil(data.totalCount / data.pageSize),
  }
}

export async function fetchPublicBundle(id: string, signal?: AbortSignal): Promise<BundleDetail> {
  const raw = await apiFetch<unknown>({
    path: `api/bundles/${encodeURIComponent(id)}`,
    method: 'GET',
    signal,
  })
  return bundleDetailResponseSchema.parse(raw)
}

export type SellerMutationResult =
  | { ok: true }
  | { ok: false; message: string; fieldErrors?: Record<string, string> }

export type SellerCreateBundleResult =
  | { ok: true; data: CreateBundleResponse }
  | { ok: false; message: string; fieldErrors?: Record<string, string> }

export async function fetchMyBundles(signal?: AbortSignal): Promise<PagedBundles> {
  const params = new URLSearchParams({
    page: '1',
    pageSize: '50',
    sortBy: 'UpdatedAt',
    sortDirection: 'DESC',
  })
  const result = await fetchBffJson(`/api/seller/bundles?${params}`, pagedBundlesResponseSchema, {
    method: 'GET',
    signal,
  })
  if (!result.ok) {
    if (result.status === 401) throw new Error('SIGN_IN_REQUIRED')
    throw new Error(result.message)
  }
  return result.data
}

export async function fetchMyBundle(id: string, signal?: AbortSignal): Promise<BundleDetail> {
  const result = await fetchBffJson(
    `/api/seller/bundles/${encodeURIComponent(id)}`,
    bundleDetailResponseSchema,
    { method: 'GET', signal },
  )
  if (!result.ok) {
    if (result.status === 401) throw new Error('SIGN_IN_REQUIRED')
    throw new Error(result.message)
  }
  return result.data
}

export async function createSellerBundle(
  body: CreateBundleBody,
): Promise<SellerCreateBundleResult> {
  const result = await fetchBffJson('/api/seller/bundles', createBundleResponseSchema, {
    method: 'POST',
    body: JSON.stringify({
      title: body.title,
      description: body.description?.trim() ? body.description.trim() : null,
      price: body.price,
      assetIds: body.assetIds,
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
  return { ok: true, data: result.data }
}

export async function reviseSellerBundle(
  id: string,
  body: ReviseBundleBody,
): Promise<SellerCreateBundleResult> {
  const result = await fetchBffJson(
    `/api/seller/bundles/${encodeURIComponent(id)}`,
    createBundleResponseSchema,
    {
      method: 'PUT',
      body: JSON.stringify({
        title: body.title,
        description: body.description?.trim() ? body.description.trim() : null,
        price: body.price,
        assetIds: body.assetIds,
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
  return { ok: true, data: result.data }
}

export async function archiveSellerBundle(id: string): Promise<SellerMutationResult> {
  const result = await fetchBffJson(
    `/api/seller/bundles/${encodeURIComponent(id)}/archive`,
    z.unknown(),
    {
      method: 'POST',
    },
  )
  if (!result.ok) return { ok: false, message: result.message }
  return { ok: true }
}

export async function restoreSellerBundle(id: string): Promise<SellerMutationResult> {
  const result = await fetchBffJson(
    `/api/seller/bundles/${encodeURIComponent(id)}/restore`,
    z.unknown(),
    {
      method: 'POST',
    },
  )
  if (!result.ok) return { ok: false, message: result.message }
  return { ok: true }
}
