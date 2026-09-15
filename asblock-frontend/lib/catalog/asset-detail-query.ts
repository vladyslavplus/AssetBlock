import { apiFetch } from '@/lib/http/api-client'
import type {
  AssetDetailItemApi,
  AssetListItemApi,
  AssetVersionSummaryApi,
  PagedResultDto,
  ReviewListItemApi,
} from '@/lib/catalog/assets-api'
import { mapApiAssetToListItem, mapReviewApiToUi } from '@/lib/catalog/assets-api'
import type { AssetListItem } from '@/lib/catalog/asset-types'
import type { AssetReview } from '@/lib/catalog/catalog-utils'
import type { SimilarAssetsExposure } from '@/lib/analytics/recommendation-telemetry'

export const SIMILAR_ASSETS_DEFAULT_LIMIT = 6

export const SIMILAR_ASSETS_MODES = ['similarity', 'popularity'] as const

export type SimilarAssetsMode = (typeof SIMILAR_ASSETS_MODES)[number]

export const SIMILAR_ASSETS_DEFAULT_MODE: SimilarAssetsMode = 'similarity'

export const assetKeys = {
  all: ['assets'] as const,
  detail: (id: string) => [...assetKeys.all, 'detail', id] as const,
  reviews: (id: string) => [...assetKeys.all, 'reviews', id] as const,
  versions: (id: string) => [...assetKeys.all, 'versions', id] as const,
  similarAll: [...(['assets'] as const), 'similar'] as const,
  similar: (
    id: string,
    limit: number = SIMILAR_ASSETS_DEFAULT_LIMIT,
    mode: SimilarAssetsMode = SIMILAR_ASSETS_DEFAULT_MODE,
  ) => [...assetKeys.similarAll, id, limit, mode] as const,
  personalSimilar: (
    userId: string,
    id: string,
    limit: number = SIMILAR_ASSETS_DEFAULT_LIMIT,
    mode: SimilarAssetsMode = SIMILAR_ASSETS_DEFAULT_MODE,
  ) => [...assetKeys.similarAll, id, limit, mode, 'personal', userId] as const,
}

/** Matches personal similar-asset keys (`...,'personal',userId`) for user-scoped clearing. */
export function isPersonalSimilarKey(queryKey: readonly unknown[]): boolean {
  return (
    queryKey.length === 7 &&
    queryKey[0] === 'assets' &&
    queryKey[1] === 'similar' &&
    queryKey[5] === 'personal' &&
    typeof queryKey[6] === 'string'
  )
}

export async function fetchAssetDetailPublic(assetId: string): Promise<AssetDetailItemApi> {
  return apiFetch<AssetDetailItemApi>({
    path: `api/assets/${encodeURIComponent(assetId)}`,
    method: 'GET',
  })
}

export async function fetchAssetVersionsPublic(assetId: string): Promise<AssetVersionSummaryApi[]> {
  const data = await apiFetch<AssetVersionSummaryApi[]>({
    path: `api/assets/${encodeURIComponent(assetId)}/versions`,
    method: 'GET',
  })
  return Array.isArray(data) ? data : []
}

export async function fetchAssetReviewsPublic(assetId: string): Promise<AssetReview[]> {
  const qs = new URLSearchParams({
    page: '1',
    pageSize: '50',
    sortBy: 'CreatedAt',
    sortDirection: 'DESC',
  })
  const data = await apiFetch<PagedResultDto<ReviewListItemApi>>({
    path: `api/reviews/assets/${encodeURIComponent(assetId)}/reviews?${qs.toString()}`,
    method: 'GET',
  })
  return (data.items ?? []).map(mapReviewApiToUi)
}

export interface SimilarAssetExplanation {
  assetId: string
  code: string
  text: string
}

export interface SimilarAssetsQueryResult {
  items: AssetListItem[]
  exposure: SimilarAssetsExposure | null
  explanations: SimilarAssetExplanation[]
}

function parseSimilarExplanation(entry: unknown): SimilarAssetExplanation | null {
  if (typeof entry !== 'object' || entry === null) {
    return null
  }
  const { assetId, code, text } = entry as {
    assetId?: unknown
    code?: unknown
    text?: unknown
  }
  if (typeof assetId !== 'string' || assetId.length === 0) {
    return null
  }
  if (typeof code !== 'string' || code.length === 0) {
    return null
  }
  if (typeof text !== 'string' || text.length === 0) {
    return null
  }
  return { assetId, code, text }
}

function reconcileSimilarExplanations(
  raw: unknown,
  items: AssetListItem[],
): SimilarAssetExplanation[] {
  if (items.length === 0 || !Array.isArray(raw)) {
    return []
  }

  const returnedIds = new Set(items.map((item) => item.id))
  const firstByAssetId = new Map<string, SimilarAssetExplanation>()
  for (const entry of raw) {
    const parsed = parseSimilarExplanation(entry)
    if (!parsed || !returnedIds.has(parsed.assetId) || firstByAssetId.has(parsed.assetId)) {
      continue
    }
    firstByAssetId.set(parsed.assetId, parsed)
  }

  const ordered: SimilarAssetExplanation[] = []
  const emitted = new Set<string>()
  for (const item of items) {
    const explanation = firstByAssetId.get(item.id)
    if (!explanation || emitted.has(item.id)) {
      continue
    }
    ordered.push(explanation)
    emitted.add(item.id)
  }
  return ordered
}

function toSimilarAssetsQueryResult(data: {
  items?: AssetListItemApi[]
  exposure?: SimilarAssetsExposure | null
  explanations?: unknown
}): SimilarAssetsQueryResult {
  const items = (data.items ?? []).map(mapApiAssetToListItem)
  return {
    items,
    exposure: mapSimilarExposure(data.exposure),
    explanations: reconcileSimilarExplanations(data.explanations, items),
  }
}

export async function fetchSimilarAssetsPublic(
  assetId: string,
  limit: number = SIMILAR_ASSETS_DEFAULT_LIMIT,
  mode: SimilarAssetsMode = SIMILAR_ASSETS_DEFAULT_MODE,
): Promise<SimilarAssetsQueryResult> {
  const qs = new URLSearchParams({ limit: String(limit), mode })
  const data = await apiFetch<{
    items?: AssetListItemApi[]
    exposure?: SimilarAssetsExposure | null
    explanations?: unknown
  }>({
    path: `api/assets/${encodeURIComponent(assetId)}/similar?${qs.toString()}`,
    method: 'GET',
  })
  return toSimilarAssetsQueryResult(data)
}

function mapSimilarExposure(
  raw: SimilarAssetsExposure | null | undefined,
): SimilarAssetsExposure | null {
  if (
    !raw ||
    typeof raw.id !== 'string' ||
    typeof raw.rankingVersion !== 'string' ||
    typeof raw.expiresAt !== 'string' ||
    typeof raw.token !== 'string'
  ) {
    return null
  }
  return {
    id: raw.id,
    rankingVersion: raw.rankingVersion,
    expiresAt: raw.expiresAt,
    token: raw.token,
  }
}

export function similarAssetsQueryOptions(
  assetId: string,
  limit: number = SIMILAR_ASSETS_DEFAULT_LIMIT,
  mode: SimilarAssetsMode = SIMILAR_ASSETS_DEFAULT_MODE,
) {
  return {
    queryKey: assetKeys.similar(assetId, limit, mode),
    queryFn: () => fetchSimilarAssetsPublic(assetId, limit, mode),
    staleTime: 0,
    refetchOnMount: true,
    refetchOnWindowFocus: true,
  }
}

export async function fetchPersonalSimilarAssets(
  assetId: string,
  limit: number = SIMILAR_ASSETS_DEFAULT_LIMIT,
  mode: SimilarAssetsMode = SIMILAR_ASSETS_DEFAULT_MODE,
): Promise<SimilarAssetsQueryResult> {
  const qs = new URLSearchParams({ limit: String(limit), mode })
  const res = await fetch(
    `/api/account/assets/${encodeURIComponent(assetId)}/similar?${qs.toString()}`,
    { credentials: 'include' },
  )
  const json: unknown = await res.json().catch(() => null)
  if (!res.ok) {
    throw new Error('Could not load personalized recommendations.')
  }
  const data = (typeof json === 'object' && json !== null ? json : {}) as {
    items?: AssetListItemApi[]
    exposure?: SimilarAssetsExposure | null
    explanations?: unknown
  }
  return toSimilarAssetsQueryResult(data)
}

export function personalSimilarAssetsQueryOptions(
  userId: string,
  assetId: string,
  limit: number = SIMILAR_ASSETS_DEFAULT_LIMIT,
  mode: SimilarAssetsMode = SIMILAR_ASSETS_DEFAULT_MODE,
) {
  return {
    queryKey: assetKeys.personalSimilar(userId, assetId, limit, mode),
    queryFn: () => fetchPersonalSimilarAssets(assetId, limit, mode),
    staleTime: 0,
    refetchOnMount: true,
    refetchOnWindowFocus: true,
  }
}
