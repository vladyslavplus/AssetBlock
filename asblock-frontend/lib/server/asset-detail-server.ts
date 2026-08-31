import { cache } from 'react'
import type {
  AssetDetailItemApi,
  PagedResultDto,
  ReviewListItemApi,
} from '@/lib/catalog/assets-api'
import { mapReviewApiToUi } from '@/lib/catalog/assets-api'
import type { AssetReview } from '@/lib/catalog/catalog-utils'
import { fetchBackendPublic } from '@/lib/server/fetch-backend'

async function readJson<T>(res: Response): Promise<T | undefined> {
  const text = await res.text()
  if (!text) return undefined
  try {
    return JSON.parse(text) as T
  } catch {
    return undefined
  }
}

export const getAssetDetailCached = cache(
  async (id: string): Promise<AssetDetailItemApi | null> => {
    const res = await fetchBackendPublic(`/api/assets/${encodeURIComponent(id)}`)
    if (res.status === 404) return null
    if (!res.ok) {
      return null
    }
    const body = await readJson<AssetDetailItemApi>(res)
    return body ?? null
  },
)

export const getAssetReviewsCached = cache(async (assetId: string): Promise<AssetReview[]> => {
  const qs = new URLSearchParams({
    page: '1',
    pageSize: '50',
    sortBy: 'CreatedAt',
    sortDirection: 'DESC',
  })
  const res = await fetchBackendPublic(
    `/api/reviews/assets/${encodeURIComponent(assetId)}/reviews?${qs.toString()}`,
  )
  if (!res.ok) {
    return []
  }
  const data = await readJson<PagedResultDto<ReviewListItemApi>>(res)
  if (!data) return []
  return (data.items ?? []).map(mapReviewApiToUi)
})
