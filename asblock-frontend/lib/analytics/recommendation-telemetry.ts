import { classifyDeviceClass, isDoNotTrackEnabled } from '@/lib/analytics/telemetry-client'

export const RECOMMENDATION_IMPRESSION_MIN_RATIO = 0.5
export const RECOMMENDATION_IMPRESSION_DWELL_MS = 1000

export interface SimilarAssetsExposure {
  id: string
  rankingVersion: string
  expiresAt: string
  token: string
}

export interface RecommendationSignalInput {
  eventType: 'IMPRESSION' | 'CLICK'
  sourceAssetId: string
  targetAssetId: string
  slotPosition: number
  exposure: SimilarAssetsExposure
  candidateIds: string[]
}

const sentKeys = new Set<string>()

export function resetRecommendationSignalDedupeForTests(): void {
  sentKeys.clear()
}

function signalKey(input: RecommendationSignalInput): string {
  return `${input.exposure.id}:${input.eventType}:${input.targetAssetId}`
}

/** Fire-and-forget recommendation beacon. Never throws; skips when DNT/GPC is enabled. */
export function trackRecommendationEvent(input: RecommendationSignalInput): void {
  if (isDoNotTrackEnabled()) return
  const key = signalKey(input)
  if (sentKeys.has(key)) return
  sentKeys.add(key)

  const body = {
    eventId: crypto.randomUUID(),
    eventType: input.eventType,
    sourceAssetId: input.sourceAssetId,
    targetAssetId: input.targetAssetId,
    slotPosition: input.slotPosition,
    exposureId: input.exposure.id,
    rankingVersion: input.exposure.rankingVersion,
    expiresAt: input.exposure.expiresAt,
    exposureToken: input.exposure.token,
    candidateIds: input.candidateIds,
    deviceClass: classifyDeviceClass(),
  }

  void fetch('/api/analytics/recommendation-events', {
    method: 'POST',
    keepalive: true,
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).catch(() => {
    // Telemetry must never affect UX.
  })
}
