import { getServerApiBaseUrl } from '@/lib/http/api-config'

function getErrnoCode(value: unknown): string | undefined {
  if (value && typeof value === 'object' && 'code' in value) {
    const c = (value as { code?: unknown }).code
    return typeof c === 'string' ? c : undefined
  }
  return undefined
}

/**
 * Short hint for logs (no secrets). Uses the configured server API base URL.
 */
export function apiBaseUrlForLogs(): string {
  try {
    return getServerApiBaseUrl()
  } catch {
    return '(not configured)'
  }
}

/**
 * User-facing explanation when server-side `fetch` to the backend throws (TLS, DNS, refused, etc.).
 * Avoid leaking stack traces; localhost hints are intentional for dev.
 */
export function describeServerFetchError(error: unknown): string {
  const base = apiBaseUrlForLogs()

  if (!(error instanceof Error)) {
    return `Could not reach the API (${base}). Check ASSETBLOCK_API_BASE_URL or NEXT_PUBLIC_API_BASE_URL.`
  }

  const cause = error.cause
  const causeCode = cause ? getErrnoCode(cause) : undefined
  const causeMsg = cause instanceof Error ? cause.message : ''

  if (
    causeCode === 'DEPTH_ZERO_SELF_SIGNED_CERT' ||
    causeCode === 'UNABLE_TO_VERIFY_LEAF_SIGNATURE' ||
    /self-signed certificate/i.test(causeMsg) ||
    /self-signed certificate/i.test(error.message)
  ) {
    return 'HTTPS certificate is not trusted by the Next.js server (common with local dev certs).'
  }

  if (causeCode === 'ECONNREFUSED') {
    return `Cannot connect to the API at ${base} (connection refused). Start the backend or fix the URL.`
  }

  if (causeCode === 'ENOTFOUND') {
    return `API host could not be resolved. Check ASSETBLOCK_API_BASE_URL or NEXT_PUBLIC_API_BASE_URL (${base}).`
  }

  if (causeCode === 'ETIMEDOUT' || causeCode === 'UND_ERR_CONNECT_TIMEOUT') {
    return `Connection to the API timed out (${base}).`
  }

  if (/fetch failed/i.test(error.message)) {
    return `Could not reach the API (${base}). Check that the backend is running and the URL is correct.`
  }

  return `Could not reach the API (${base}). ${error.message}`
}
