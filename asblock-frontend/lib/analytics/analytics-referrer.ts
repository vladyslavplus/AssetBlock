/** Matches backend AnalyticsTelemetryConstants.REFERRER_HOST_MAX_LENGTH. */
export const ANALYTICS_REFERRER_HOST_MAX_LENGTH = 253

const MAX_LABEL_LENGTH = 63

function isHostChar(c: string): boolean {
  const code = c.charCodeAt(0)
  return (
    (code >= 97 && code <= 122) ||
    (code >= 65 && code <= 90) ||
    (code >= 48 && code <= 57) ||
    c === '-'
  )
}

function isValidHost(host: string): boolean {
  if (host.startsWith('.') || host.startsWith('-') || host.endsWith('.') || host.endsWith('-')) {
    return false
  }

  let labelLength = 0
  for (const c of host) {
    if (c === '.') {
      if (labelLength === 0) return false
      labelLength = 0
      continue
    }
    if (!isHostChar(c)) return false
    labelLength += 1
    if (labelLength > MAX_LABEL_LENGTH) return false
  }

  return labelLength > 0
}

/**
 * Reduces an untrusted referrer value to a bare lowercase ASCII host (scheme/path/query/port stripped).
 * Returns null when the input is absent or not a syntactically valid ASCII host.
 */
export function normalizeAnalyticsReferrerHost(raw: string | null | undefined): string | null {
  if (raw == null || raw.trim().length === 0) {
    return null
  }

  let value = raw.trim()

  const schemeIndex = value.indexOf('://')
  if (schemeIndex >= 0) {
    value = value.slice(schemeIndex + 3)
  }

  const authorityEnd = value.search(/[/?#]/)
  if (authorityEnd >= 0) {
    value = value.slice(0, authorityEnd)
  }

  const userInfoIndex = value.lastIndexOf('@')
  if (userInfoIndex >= 0) {
    value = value.slice(userInfoIndex + 1)
  }

  const portIndex = value.lastIndexOf(':')
  if (portIndex >= 0) {
    value = value.slice(0, portIndex)
  }

  if (value.length === 0 || value.length > ANALYTICS_REFERRER_HOST_MAX_LENGTH) {
    return null
  }

  return isValidHost(value) ? value.toLowerCase() : null
}
