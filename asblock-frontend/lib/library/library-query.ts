import { getApiErrorMessage } from '@/lib/http/api-errors'
import type { PurchaseLibraryItem, PagedPurchaseLibraryDto } from '@/lib/library/purchase-types'

export const libraryKeys = {
  all: ['library'] as const,
  purchases: () => [...libraryKeys.all, 'purchases'] as const,
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

export async function fetchLibraryPurchases(): Promise<LibraryPurchasesResult> {
  const res = await fetch('/api/account/library', { credentials: 'include', cache: 'no-store' })
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
    hasUserReviewed: Boolean(row.hasUserReviewed),
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

export async function fetchLibraryPurchasesOrThrow(): Promise<PagedPurchaseLibraryDto> {
  const r = await fetchLibraryPurchases()
  if (!r.ok) {
    throw new LibraryFetchError(r.status, r.message)
  }
  return r.data
}
