import { getApiErrorMessage } from '@/lib/http/api-errors'
import {
  assetVersionsResponseSchema,
  pagedPurchaseLibraryResponseSchema,
  type AssetVersionSummary,
  type PagedPurchaseLibraryDto,
} from '@/lib/library/library-schemas'

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

  const result = pagedPurchaseLibraryResponseSchema.safeParse(parsed)
  if (!result.success) {
    return {
      ok: false,
      status: 502,
      message: 'Invalid library response from server.',
    }
  }

  return {
    ok: true,
    data: result.data,
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

export async function fetchLibraryAssetVersions(assetId: string): Promise<AssetVersionSummary[]> {
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

  const result = assetVersionsResponseSchema.safeParse(parsed)
  if (!result.success) {
    throw new LibraryFetchError(502, 'Invalid asset versions response from server.')
  }

  return result.data
}
