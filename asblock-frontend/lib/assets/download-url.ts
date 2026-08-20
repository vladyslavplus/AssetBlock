/** Same-origin BFF download URL; optional versionId selects a specific entitled version. */
export function buildAssetDownloadUrl(assetId: string, versionId?: string | null): string {
  const base = `/api/assets/${encodeURIComponent(assetId)}/download`
  const trimmed = versionId?.trim()
  if (!trimmed) return base
  const params = new URLSearchParams({ versionId: trimmed })
  return `${base}?${params.toString()}`
}
