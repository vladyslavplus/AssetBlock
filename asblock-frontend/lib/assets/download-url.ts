import type { Route } from 'next'

/** Same-origin BFF download URL; optional versionId selects a specific entitled version. */
export function buildAssetDownloadUrl(assetId: string, versionId?: string | null): Route {
  const base = `/api/assets/${encodeURIComponent(assetId)}/download`
  const trimmed = versionId?.trim()
  if (!trimmed) return base as Route
  const params = new URLSearchParams({ versionId: trimmed })
  return `${base}?${params.toString()}` as Route
}
