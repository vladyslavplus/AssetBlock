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
    case 'PURCHASE_COMPLETED':
      return 'Purchase completed'
    case 'DOWNLOAD_READY':
      return 'Download ready'
    case 'ASSET_SOLD':
      return 'Asset sold'
    case 'REVIEW_RECEIVED':
      return 'New review'
    default:
      return 'Notification'
  }
}

export function getNotificationBody(_kind: string, metadataJson: string): string {
  const parsed = tryParseJson(metadataJson)
  const r = asRecord(parsed)
  if (!r) {
    return ''
  }
  const title = pickString(r, 'assetTitle')
  if (title) {
    return title
  }
  return ''
}

export function formatHubToastMessage(method: string, payload: unknown): string {
  const title = getNotificationTitle(method)
  const r = asRecord(payload)
  const assetTitle = r ? pickString(r, 'assetTitle') : undefined
  if (assetTitle) {
    return `${title}: ${assetTitle}`
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
