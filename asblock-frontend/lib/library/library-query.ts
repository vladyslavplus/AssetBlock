import { getApiErrorMessage } from '@/lib/http/api-errors'
import type { AssetVersionSummaryApi } from '@/lib/catalog/assets-api'
import {
  normalizePurchaseSource,
  type PurchaseLibraryItem,
  type PagedPurchaseLibraryDto,
} from '@/lib/library/purchase-types'

export interface LibraryPurchasesParams {
  page?: number
  pageSize?: number
}

export const libraryKeys = {
  all: ['library'] as const,
  purchases: (params?: LibraryPurchasesParams) =>
    params
      ? ([...libraryKeys.all, 'purchases', params] as const)
      : ([...libraryKeys.all, 'purchases'] as const),
  assetVersions: (assetId: string) => [...libraryKeys.all, 'versions', assetId] as const,
}

export type LibraryPurchasesResult =
  | { ok: true; data: PagedPurchaseLibraryDto }
  | { ok: false; status: number; message: string }

/** For TanStack Query: throws with `cause` holding status when the BFF returns an error. */
export class LibraryFetchError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'LibraryFetchError'
    this.status = status
  }
}

export async function fetchLibraryPurchases(
  params?: LibraryPurchasesParams,
): Promise<LibraryPurchasesResult> {
  const qs = new URLSearchParams()
  if (params?.page) qs.set('page', String(Math.max(1, params.page)))
  if (params?.pageSize) qs.set('pageSize', String(Math.max(1, params.pageSize)))
  const queryStr = qs.toString()
  const path = queryStr ? `/api/account/library?${queryStr}` : '/api/account/library'
  const res = await fetch(path, { credentials: 'include', cache: 'no-store' })
  const text = await res.text()
  let parsed: unknown = text
  if (text.length > 0) {
    try {
      parsed = JSON.parse(text) as unknown
    } catch {
      parsed = text
    }
  }
  if (!res.ok) {
    return {
      ok: false,
      status: res.status,
      message: getApiErrorMessage(
        parsed,
        typeof parsed === 'string' && parsed.length > 0
          ? parsed
          : `Could not load library (${res.status}).`,
      ),
    }
  }
  const data = parsed as PagedPurchaseLibraryDto
  const rawItems = Array.isArray(data.items) ? data.items : []
  const items: PurchaseLibraryItem[] = rawItems.map((row) => ({
    ...row,
    orderId: row.orderId ?? '',
    hasUserReviewed: Boolean(row.hasUserReviewed),
    purchasedVersionNumber: Number(row.purchasedVersionNumber),
    purchasedVersionId: row.purchasedVersionId,
    latestEntitledVersionNumber: Number(row.latestEntitledVersionNumber),
    latestEntitledVersionId: row.latestEntitledVersionId,
    hasUpdate: Boolean(row.hasUpdate),
    pricePaid: Number(row.pricePaid),
    currency: row.currency ?? 'usd',
    source: normalizePurchaseSource(row.source),
    bundleId: row.bundleId ?? null,
    bundleTitle: row.bundleTitle ?? null,
  }))
  return {
    ok: true,
    data: {
      items,
      totalCount: Number(data.totalCount) || 0,
      page: Number(data.page) || 1,
      pageSize: Number(data.pageSize) || 0,
    },
  }
}

export async function fetchLibraryPurchasesOrThrow(
  params?: LibraryPurchasesParams,
): Promise<PagedPurchaseLibraryDto> {
  const normalizedParams =
    params && typeof params === 'object' && ('page' in params || 'pageSize' in params)
      ? params
      : undefined
  const r = await fetchLibraryPurchases(normalizedParams)
  if (!r.ok) {
    throw new LibraryFetchError(r.status, r.message)
  }
  return r.data
}

export async function fetchLibraryAssetVersions(
  assetId: string,
): Promise<AssetVersionSummaryApi[]> {
  const res = await fetch(`/api/assets/${encodeURIComponent(assetId)}/versions`, {
    credentials: 'include',
  })
  const text = await res.text()
  let parsed: unknown = text
  if (text.length > 0) {
    try {
      parsed = JSON.parse(text) as unknown
    } catch {
      parsed = text
    }
  }
  if (!res.ok) {
    throw new LibraryFetchError(
      res.status,
      getApiErrorMessage(parsed, `Could not load versions (${res.status})`),
    )
  }
  return Array.isArray(parsed) ? (parsed as AssetVersionSummaryApi[]) : []
}
