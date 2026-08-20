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
  switch (k) {
    case 'ORDER_READY':
      return 'Order ready'
    case 'DOWNLOAD_READY':
      return 'Download ready'
    case 'ASSET_SOLD':
      return 'Sale completed'
    case 'REVIEW_RECEIVED':
      return 'New review'
    default:
      return 'Notification'
  }
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

export function getNotificationHref(kindOrMethod: string, metadataJson: string): string {
  const k = normalizeNotificationKind(kindOrMethod)
  const parsed = tryParseJson(metadataJson)
  const r = asRecord(parsed)
  if (r) {
    const bundleId = pickString(r, 'bundleId')
    if (bundleId && (k === 'ORDER_READY' || k === 'ASSET_SOLD')) {
      return `/bundles/${encodeURIComponent(bundleId)}`
    }
    const assetId = pickString(r, 'assetId')
    if (assetId) {
      return `/assets/${encodeURIComponent(assetId)}`
    }
  }
  if (k === 'ORDER_READY') {
    return '/library'
  }
  return '/library'
}
