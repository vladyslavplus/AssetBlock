import { getApiErrorMessage } from '@/lib/http/api-errors'
import type { PagedPurchaseLibraryDto } from '@/lib/library/purchase-types'
import type { AuthCookieStore } from '@/lib/server/auth-cookies'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'

export type FetchMyPurchasesResult =
  | { ok: true; data: PagedPurchaseLibraryDto }
  | { ok: false; status: number; message: string }

export async function fetchMyPurchasesFromBackend(
  cookieStore: AuthCookieStore,
): Promise<FetchMyPurchasesResult> {
  const qs = new URLSearchParams({
    page: '1',
    pageSize: '100',
    sortDirection: 'DESC',
  })
  const res = await fetchBackendAuthorized(
    cookieStore,
    `/api/users/me/purchases?${qs.toString()}`,
    { method: 'GET' },
    { persistRefreshedTokens: false },
  )

  const text = await res.text()

  if (!res.ok) {
    let parsed: unknown = text
    if (text.length > 0) {
      try {
        parsed = JSON.parse(text) as unknown
      } catch {
        parsed = text
      }
    }
    const message = getApiErrorMessage(
      parsed,
      typeof parsed === 'string' && parsed.length > 0
        ? parsed
        : `Could not load library (${res.status}).`,
    )
    return { ok: false, status: res.status, message }
  }

  const data = JSON.parse(text) as PagedPurchaseLibraryDto
  const rawItems = Array.isArray(data.items) ? data.items : []
  const items = rawItems.map((row) => ({
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
