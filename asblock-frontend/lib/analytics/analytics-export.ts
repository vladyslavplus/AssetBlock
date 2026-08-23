import type { AnalyticsProductTypeFilter, AnalyticsUtcRange } from '@/lib/analytics/analytics-types'
import { getApiErrorMessage, parseApiErrorBody, readApiResponseBody } from '@/lib/http/api-errors'
import { ApiRequestError } from '@/lib/http/api-client'
import { isAbortError } from '@/lib/http/is-abort-error'

function parseContentDispositionFilename(header: string | null): string | null {
  if (!header) return null

  const utf8Match = /filename\*=UTF-8''([^;]+)/i.exec(header)
  if (utf8Match?.[1]) {
    try {
      return decodeURIComponent(utf8Match[1])
    } catch {
      return utf8Match[1]
    }
  }

  const plainMatch = /filename="([^"]+)"/i.exec(header)
  if (plainMatch?.[1]) return plainMatch[1]

  const unquotedMatch = /filename=([^;]+)/i.exec(header)
  if (unquotedMatch?.[1]) return unquotedMatch[1].trim()

  return null
}

function exportErrorMessage(status: number, body: unknown): string {
  if (status === 401) return 'Please sign in to export sales.'
  if (status === 403) return 'Verify your email address to export sales.'
  if (status === 429) return 'Too many export requests. Please wait and try again.'

  const parsed = parseApiErrorBody(body)
  if (parsed?.code === 'ERR_ANALYTICS_EXPORT_TOO_LARGE') {
    return 'This export exceeds the maximum allowed row count. Narrow the date range or filter.'
  }

  return getApiErrorMessage(body, `Export failed (${status}).`)
}

/** Downloads sales CSV via BFF; not cached. Never logs CSV body. */
export async function downloadAnalyticsSalesExport(
  range: AnalyticsUtcRange,
  productType: AnalyticsProductTypeFilter,
  signal?: AbortSignal,
): Promise<void> {
  const params = new URLSearchParams({
    from: range.from,
    to: range.to,
    productType,
  })

  let response: Response
  try {
    response = await fetch(`/api/seller/analytics/sales/export?${params.toString()}`, {
      method: 'GET',
      credentials: 'include',
      signal,
    })
  } catch (error) {
    if (isAbortError(error, signal)) throw error
    throw new ApiRequestError('Network error while exporting sales.', 0, null)
  }

  if (!response.ok) {
    const body = await readApiResponseBody(response)
    throw new ApiRequestError(exportErrorMessage(response.status, body), response.status, body)
  }

  let blob: Blob
  try {
    blob = await response.blob()
  } catch (error) {
    if (isAbortError(error, signal)) throw error
    throw error
  }
  const filename =
    parseContentDispositionFilename(response.headers.get('Content-Disposition')) ??
    `assetblock-sales-${range.from}-${range.to}.csv`

  const objectUrl = URL.createObjectURL(blob)
  try {
    const anchor = document.createElement('a')
    anchor.href = objectUrl
    anchor.download = filename
    anchor.rel = 'noopener'
    anchor.style.display = 'none'
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
  } finally {
    URL.revokeObjectURL(objectUrl)
  }
}
