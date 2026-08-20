import type {
  AnalyticsDeviceClass,
  AnalyticsTrafficSource,
} from '@/lib/analytics/telemetry-constants'
import { resolveReferrerHostFromDocument } from '@/lib/analytics/telemetry-source'

export type TrackAnalyticsEventInput =
  | {
      eventType: 'ASSET_VIEW'
      source: AnalyticsTrafficSource
      assetId: string
    }
  | {
      eventType: 'BUNDLE_VIEW'
      source: AnalyticsTrafficSource
      bundleId: string
    }
  | {
      eventType: 'COLLECTION_VIEW'
      source: AnalyticsTrafficSource
      collectionId: string
    }
  | {
      eventType: 'COLLECTION_ITEM_CLICK'
      source: AnalyticsTrafficSource
      collectionId: string
      assetId: string
    }
  | {
      eventType: 'DOWNLOAD_REQUESTED'
      source: AnalyticsTrafficSource
      assetId: string
      assetVersionId: string
    }

export function isDoNotTrackEnabled(): boolean {
  if (typeof navigator === 'undefined') return true
  if (navigator.doNotTrack === '1') return true
  if (
    typeof (navigator as Navigator & { globalPrivacyControl?: boolean }).globalPrivacyControl ===
    'boolean'
  ) {
    return (
      (navigator as Navigator & { globalPrivacyControl?: boolean }).globalPrivacyControl === true
    )
  }
  return false
}

export function classifyDeviceClass(): AnalyticsDeviceClass {
  if (typeof window === 'undefined') return 'UNKNOWN'
  const width = window.innerWidth
  if (!Number.isFinite(width) || width <= 0) return 'UNKNOWN'
  if (width < 768) return 'MOBILE'
  if (width < 1024) return 'TABLET'
  return 'DESKTOP'
}

/** Fire-and-forget analytics beacon. Never throws; skips entirely when DNT is enabled. */
export function trackAnalyticsEvent(input: TrackAnalyticsEventInput): void {
  if (isDoNotTrackEnabled()) return

  const referrerHost = input.source === 'EXTERNAL' ? resolveReferrerHostFromDocument() : undefined
  const body = {
    eventId: crypto.randomUUID(),
    eventType: input.eventType,
    source: input.source,
    deviceClass: classifyDeviceClass(),
    ...(referrerHost ? { referrerHost } : {}),
    ...('assetId' in input ? { assetId: input.assetId } : {}),
    ...('assetVersionId' in input ? { assetVersionId: input.assetVersionId } : {}),
    ...('bundleId' in input ? { bundleId: input.bundleId } : {}),
    ...('collectionId' in input ? { collectionId: input.collectionId } : {}),
  }

  void fetch('/api/analytics/events', {
    method: 'POST',
    keepalive: true,
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).catch(() => {
    // Telemetry must never affect UX.
  })
}
