'use client'

import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AssetCard } from '@/components/assets/asset-card'
import { AssetCardGridSkeleton } from '@/components/assets/asset-card-skeleton'
import { Button } from '@/components/ui/button'
import {
  SIMILAR_ASSETS_DEFAULT_LIMIT,
  SIMILAR_ASSETS_DEFAULT_MODE,
  personalSimilarAssetsQueryOptions,
  similarAssetsQueryOptions,
  type SimilarAssetsMode,
} from '@/lib/catalog/asset-detail-query'
import {
  RECOMMENDATION_IMPRESSION_DWELL_MS,
  RECOMMENDATION_IMPRESSION_MIN_RATIO,
  trackRecommendationEvent,
  type SimilarAssetsExposure,
} from '@/lib/analytics/recommendation-telemetry'
import { useOptionalAuth } from '@/components/auth/auth-context'
import { accountKeys, fetchRecommendationPreferences } from '@/lib/account/account-query'
import type { AssetListItem } from '@/lib/catalog/asset-types'

interface SimilarAssetsBlockProps {
  assetId: string
  limit?: number
}

export function SimilarAssetsBlock({
  assetId,
  limit = SIMILAR_ASSETS_DEFAULT_LIMIT,
}: SimilarAssetsBlockProps) {
  const [mode, setMode] = useState<SimilarAssetsMode>(SIMILAR_ASSETS_DEFAULT_MODE)
  const auth = useOptionalAuth()
  const userId = auth?.user?.id ?? null
  const preferencesQuery = useQuery({
    queryKey: accountKeys.recommendationPreferences(),
    queryFn: () => fetchRecommendationPreferences(),
    enabled: Boolean(userId),
    retry: false,
  })
  // No similar request fires until auth and (for signed-in users) preferences resolve,
  // so a delayed opt-in never triggers an early public request or impression.
  // Preference errors fail closed for personalization (never personal) and fail open
  // for content (public Phase A order); the error is never treated as a saved opt-out.
  // Personal requires a successful opt-in read: stale cached data surviving a failed
  // refetch must not keep the personal query active.
  const ready =
    (auth === null || auth.status !== 'loading') &&
    (!userId || preferencesQuery.isSuccess || preferencesQuery.isError)
  const usePersonal =
    Boolean(userId) && preferencesQuery.isSuccess && preferencesQuery.data.isPersonalized === true
  const publicQuery = useQuery({
    ...similarAssetsQueryOptions(assetId, limit, mode),
    enabled: ready && !usePersonal,
  })
  const personalQuery = useQuery({
    ...personalSimilarAssetsQueryOptions(userId ?? '', assetId, limit, mode),
    enabled: ready && usePersonal && userId !== null,
  })
  const query = usePersonal && userId ? personalQuery : publicQuery
  const gridRef = useRef<HTMLDivElement>(null)

  const items = query.data?.items ?? []
  const exposure = query.data?.exposure ?? null
  const explanationByAssetId = new Map(
    (query.data?.explanations ?? []).map((explanation) => [explanation.assetId, explanation.text]),
  )

  useEffect(() => {
    const visibleItems = query.data?.items ?? []
    const currentExposure = query.data?.exposure ?? null
    if (
      !currentExposure ||
      visibleItems.length === 0 ||
      typeof IntersectionObserver === 'undefined'
    ) {
      return
    }

    const root = gridRef.current
    if (!root) return

    const timers = new Map<Element, number>()
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          const targetId = (entry.target as HTMLElement).dataset.recommendationTargetId
          const slotRaw = (entry.target as HTMLElement).dataset.recommendationSlot
          if (!targetId || slotRaw === undefined) continue
          const slotPosition = Number(slotRaw)
          if (!Number.isInteger(slotPosition)) continue

          if (
            entry.isIntersecting &&
            entry.intersectionRatio >= RECOMMENDATION_IMPRESSION_MIN_RATIO
          ) {
            if (timers.has(entry.target)) continue
            const timeoutId = window.setTimeout(() => {
              timers.delete(entry.target)
              trackRecommendationEvent({
                eventType: 'IMPRESSION',
                sourceAssetId: assetId,
                targetAssetId: targetId,
                slotPosition,
                exposure: currentExposure,
                candidateIds: visibleItems.map((item) => item.id),
              })
            }, RECOMMENDATION_IMPRESSION_DWELL_MS)
            timers.set(entry.target, timeoutId)
          } else {
            const timeoutId = timers.get(entry.target)
            if (timeoutId !== undefined) {
              window.clearTimeout(timeoutId)
              timers.delete(entry.target)
            }
          }
        }
      },
      { threshold: [0, RECOMMENDATION_IMPRESSION_MIN_RATIO, 1] },
    )

    for (const node of root.querySelectorAll('[data-recommendation-target-id]')) {
      observer.observe(node)
    }

    return () => {
      observer.disconnect()
      for (const timeoutId of timers.values()) {
        window.clearTimeout(timeoutId)
      }
    }
  }, [assetId, query.data])

  if (query.isPending) {
    return (
      <section className="mt-10 space-y-4" aria-busy="true" aria-label="Loading similar assets">
        <div className="flex items-center justify-between gap-2">
          <h2 className="text-lg font-semibold text-foreground">Similar assets</h2>
          <ModeSwitch mode={mode} onChange={setMode} />
        </div>
        <AssetCardGridSkeleton count={limit} />
      </section>
    )
  }

  if (query.isError) {
    return (
      <section className="mt-10 space-y-3" aria-live="polite">
        <div className="flex items-center justify-between gap-2">
          <h2 className="text-lg font-semibold text-foreground">Similar assets</h2>
          <ModeSwitch mode={mode} onChange={setMode} />
        </div>
        <p className="text-sm text-muted-foreground">Similar assets are unavailable.</p>
        <Button type="button" size="sm" variant="outline" onClick={() => void query.refetch()}>
          Retry
        </Button>
      </section>
    )
  }

  if (items.length === 0) {
    return null
  }

  return (
    <section className="mt-10 space-y-4">
      <div className="flex items-center justify-between gap-2">
        <h2 className="text-lg font-semibold text-foreground">Similar assets</h2>
        <ModeSwitch mode={mode} onChange={setMode} />
      </div>
      <div ref={gridRef} className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3">
        {items.map((asset, index) => (
          <div
            key={asset.id}
            data-recommendation-target-id={asset.id}
            data-recommendation-slot={index}
          >
            <AssetCard
              asset={asset}
              onViewDetailsClick={() =>
                fireRecommendationClick(assetId, asset, index, items, exposure)
              }
            />
            {explanationByAssetId.get(asset.id) ? (
              <p className="mt-2 text-xs text-muted-foreground">
                {explanationByAssetId.get(asset.id)}
              </p>
            ) : null}
          </div>
        ))}
      </div>
    </section>
  )
}

function ModeSwitch({
  mode,
  onChange,
}: {
  mode: SimilarAssetsMode
  onChange: (mode: SimilarAssetsMode) => void
}) {
  return (
    <div className="flex items-center gap-1" role="group" aria-label="Recommendation ranking">
      <Button
        type="button"
        size="sm"
        variant={mode === 'similarity' ? 'secondary' : 'ghost'}
        aria-pressed={mode === 'similarity'}
        onClick={() => onChange('similarity')}
      >
        Similar
      </Button>
      <Button
        type="button"
        size="sm"
        variant={mode === 'popularity' ? 'secondary' : 'ghost'}
        aria-pressed={mode === 'popularity'}
        onClick={() => onChange('popularity')}
      >
        Popular
      </Button>
    </div>
  )
}

function fireRecommendationClick(
  sourceAssetId: string,
  asset: AssetListItem,
  slotPosition: number,
  items: AssetListItem[],
  exposure: SimilarAssetsExposure | null,
): void {
  if (!exposure) return
  trackRecommendationEvent({
    eventType: 'CLICK',
    sourceAssetId,
    targetAssetId: asset.id,
    slotPosition,
    exposure,
    candidateIds: items.map((item) => item.id),
  })
}
