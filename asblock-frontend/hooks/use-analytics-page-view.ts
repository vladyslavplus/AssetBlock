'use client'

import { useEffect, useRef } from 'react'

import type { TrackAnalyticsEventInput } from '@/lib/analytics/telemetry-client'
import { trackAnalyticsEvent } from '@/lib/analytics/telemetry-client'

/**
 * Records a page-view analytics event once per tracking key (Strict Mode safe).
 */
export function useAnalyticsPageView(
  trackingKey: string | null,
  options: TrackAnalyticsEventInput | null,
) {
  const trackedRef = useRef<string | null>(null)

  const eventType = options?.eventType ?? null
  const source = options?.source ?? null
  const assetId = options && 'assetId' in options ? options.assetId : undefined
  const assetVersionId =
    options && 'assetVersionId' in options ? options.assetVersionId : undefined
  const bundleId = options && 'bundleId' in options ? options.bundleId : undefined
  const collectionId = options && 'collectionId' in options ? options.collectionId : undefined

  useEffect(() => {
    if (!trackingKey || !eventType || !source || !options) return
    if (trackedRef.current === trackingKey) return
    trackedRef.current = trackingKey
    trackAnalyticsEvent(options)
  }, [trackingKey, eventType, source, assetId, assetVersionId, bundleId, collectionId, options])
}
