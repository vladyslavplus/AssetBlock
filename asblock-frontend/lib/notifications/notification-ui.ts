import type { Route } from 'next'
import { isModeratorRole } from '@/lib/auth/roles'
import { routes } from '@/lib/routes'
import type { NotificationKind } from '@/lib/notifications/notification-types'

const notificationTitles: Record<NotificationKind, string> = {
  DOWNLOAD_READY: 'Download ready',
  ASSET_SOLD: 'Sale completed',
  REVIEW_RECEIVED: 'New review',
  ORDER_READY: 'Order ready',
  ASSET_PROCESSING_READY: 'Listing ready',
  ASSET_PROCESSING_REJECTED: 'Listing rejected',
  ASSET_PROCESSING_FAILED: 'Listing processing failed',
  CODE_ANALYSIS_COMPLETED: 'Code analysis completed',
  MODERATION_SUBMITTED: 'Moderation submitted',
  MODERATION_REVIEW_STARTED: 'Moderation review started',
  MODERATION_CHANGES_REQUESTED: 'Moderation changes requested',
  MODERATION_APPROVED: 'Moderation approved',
  MODERATION_REJECTED: 'Moderation rejected',
  MODERATION_WITHDRAWN: 'Moderation submission withdrawn',
  MODERATION_APPEAL_UPHELD: 'Appeal upheld',
}

const ownerOutcomeKinds = new Set<string>([
  'MODERATION_REVIEW_STARTED',
  'MODERATION_CHANGES_REQUESTED',
  'MODERATION_APPROVED',
  'MODERATION_REJECTED',
  'MODERATION_APPEAL_UPHELD',
])
const ambiguousRecipientKinds = new Set<string>(['MODERATION_SUBMITTED', 'MODERATION_WITHDRAWN'])

function tryParseJson(json: string): unknown {
  try {
    return JSON.parse(json) as unknown
  } catch {
    return null
  }
}

function asRecord(v: unknown): Record<string, unknown> | null {
  return typeof v === 'object' && v !== null && !Array.isArray(v)
    ? (v as Record<string, unknown>)
    : null
}

function pickString(r: Record<string, unknown>, key: string): string | undefined {
  const v = r[key]
  return typeof v === 'string' && v.trim().length > 0 ? v.trim() : undefined
}

export function normalizeNotificationKind(input: string): string {
  if (input.includes('_')) {
    return input.toUpperCase()
  }
  return input
    .replace(/([A-Z])/g, '_$1')
    .toUpperCase()
    .replace(/^_/, '')
}

export function getNotificationTitle(kindOrMethod: string): string {
  const k = normalizeNotificationKind(kindOrMethod)
  if (k === 'MODERATION_CASE_CHANGED') return 'Moderation case updated'
  return notificationTitles[k as NotificationKind] ?? 'Notification'
}

function pickProductTitle(r: Record<string, unknown>): string | undefined {
  return pickString(r, 'productTitle') ?? pickString(r, 'assetTitle')
}

export function getNotificationBody(_kind: string, metadataJson: string): string {
  const parsed = tryParseJson(metadataJson)
  const r = asRecord(parsed)
  if (!r) {
    return ''
  }
  return pickProductTitle(r) ?? ''
}

export function formatHubToastMessage(method: string, payload: unknown): string {
  const title = getNotificationTitle(method)
  const r = asRecord(payload)
  const productTitle = r ? pickProductTitle(r) : undefined
  if (productTitle) {
    return `${title}: ${productTitle}`
  }
  return title
}

export function getNotificationAssetId(metadataJson: string): string | undefined {
  const parsed = tryParseJson(metadataJson)
  const r = asRecord(parsed)
  if (!r) {
    return undefined
  }
  const id = r.assetId
  return typeof id === 'string' && id.length > 0 ? id : undefined
}

export function getNotificationHref(
  kindOrMethod: string,
  metadataJson: string,
  role?: string | null,
): Route {
  const k = normalizeNotificationKind(kindOrMethod)
  const parsed = tryParseJson(metadataJson)
  const r = asRecord(parsed)
  if (r) {
    const submissionId = pickString(r, 'submissionId')
    if ((ownerOutcomeKinds.has(k) || ambiguousRecipientKinds.has(k)) && submissionId) {
      const moderationAssetId = pickString(r, 'assetId')
      if (ownerOutcomeKinds.has(k) && moderationAssetId)
        return routes.sellerAssetEdit(moderationAssetId)
      if (ambiguousRecipientKinds.has(k)) {
        if (isModeratorRole(role)) return routes.moderation()
        if (moderationAssetId) return routes.sellerAssetEdit(moderationAssetId)
      }
    }
    const bundleId = pickString(r, 'bundleId')
    if (bundleId && (k === 'ORDER_READY' || k === 'ASSET_SOLD')) {
      return routes.bundleDetail(bundleId)
    }
    const assetId = pickString(r, 'assetId')
    if (assetId) {
      if (
        k === 'ASSET_PROCESSING_READY' ||
        k === 'ASSET_PROCESSING_REJECTED' ||
        k === 'ASSET_PROCESSING_FAILED'
      ) {
        return routes.sellerAssetEdit(assetId)
      }
      return routes.assetDetail(assetId)
    }
  }
  if (k === 'ORDER_READY') {
    return routes.library()
  }
  return routes.library()
}
