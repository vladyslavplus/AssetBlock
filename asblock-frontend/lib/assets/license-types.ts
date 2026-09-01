import { z } from 'zod'

export const ASSET_LICENSE_CODES = ['PERSONAL', 'COMMERCIAL'] as const

export type AssetLicenseCode = (typeof ASSET_LICENSE_CODES)[number]

export const assetLicenseCodeSchema = z.enum(ASSET_LICENSE_CODES)

export const assetLicenseSummarySchema = z.object({
  code: assetLicenseCodeSchema,
  displayName: z.string().min(1),
  templateVersion: z.string().min(1),
  /** Immutable plain-text terms snapshot (platform template; not author-supplied). */
  terms: z.string(),
})

export type AssetLicenseSummaryApi = z.infer<typeof assetLicenseSummarySchema>

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
