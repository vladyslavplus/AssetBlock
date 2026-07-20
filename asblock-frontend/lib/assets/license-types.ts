export const ASSET_LICENSE_CODES = ['PERSONAL', 'COMMERCIAL'] as const

export type AssetLicenseCode = (typeof ASSET_LICENSE_CODES)[number]

export interface AssetLicenseSummaryApi {
  code: AssetLicenseCode
  displayName: string
  templateVersion: string
  /** Immutable plain-text terms snapshot (platform template; not author-supplied). */
  terms: string
}

export const ASSET_LICENSE_OPTIONS: ReadonlyArray<{
  code: AssetLicenseCode
  label: string
  summary: string
}> = [
  {
    code: 'PERSONAL',
    label: 'Personal use',
    summary: 'Private, non-commercial projects and learning.',
  },
  {
    code: 'COMMERCIAL',
    label: 'Commercial use',
    summary: 'Client work and products you sell or ship.',
  },
]

export function isAssetLicenseCode(value: string): value is AssetLicenseCode {
  return (ASSET_LICENSE_CODES as readonly string[]).includes(value)
}
