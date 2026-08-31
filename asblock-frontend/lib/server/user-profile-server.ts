import { cache } from 'react'
import { CATALOG_ASSETS_PAGE_SIZE } from '@/lib/catalog/catalog-filters'
import type { PagedResultDto, AssetListItemApi } from '@/lib/catalog/assets-api'
import { mapApiAssetToListItem } from '@/lib/catalog/assets-api'
import type { AssetListItem } from '@/lib/catalog/asset-types'
import type { UserProfilePublic } from '@/lib/profile/public-profile-types'
import { fetchBackendPublic } from '@/lib/server/fetch-backend'

export const fetchPublicProfileByUsername = cache(
  async (username: string): Promise<UserProfilePublic | null> => {
    const trimmed = username.trim()
    if (!trimmed) {
      return null
    }
    const res = await fetchBackendPublic(`/api/users/${encodeURIComponent(trimmed)}`)
    if (res.status === 404 || !res.ok) {
      return null
    }
    return (await res.json().catch(() => null)) as UserProfilePublic | null
  },
)

export interface AuthorCatalogPageResult {
  items: AssetListItem[]
  totalCount: number
  page: number
  pageSize: number
  totalPages: number
}

export async function fetchAuthorAssetsPage(
  authorId: string,
  page: number,
): Promise<AuthorCatalogPageResult> {
  const safePage = Math.max(1, Math.floor(page))
  const qs = new URLSearchParams({
    page: String(safePage),
    pageSize: String(CATALOG_ASSETS_PAGE_SIZE),
    sortBy: 'CreatedAt',
    sortDirection: 'DESC',
    authorId,
  })
  const res = await fetchBackendPublic(`/api/assets?${qs.toString()}`)
  if (!res.ok) {
    return {
      items: [],
      totalCount: 0,
      page: safePage,
      pageSize: CATALOG_ASSETS_PAGE_SIZE,
      totalPages: 0,
    }
  }
  const data = (await res.json().catch(() => null)) as PagedResultDto<AssetListItemApi> | null
  const totalCount = data?.totalCount ?? 0
  const totalPages =
    CATALOG_ASSETS_PAGE_SIZE > 0 ? Math.ceil(totalCount / CATALOG_ASSETS_PAGE_SIZE) : 0
  return {
    items: (data?.items ?? []).map(mapApiAssetToListItem),
    totalCount,
    page: data?.page ?? safePage,
    pageSize: CATALOG_ASSETS_PAGE_SIZE,
    totalPages,
  }
}
