import { z } from 'zod'
import {
  assetLicenseCodeSchema,
  assetLicenseSummarySchema,
  type AssetLicenseCode,
  type AssetLicenseSummaryApi,
} from '@/lib/assets/license-types'

export const purchaseSourceSchema = z.enum(['ASSET', 'BUNDLE'])
export type PurchaseSource = z.infer<typeof purchaseSourceSchema>

export const isoDateTimeSchema = z.string().datetime({ offset: true })

export const purchaseLibraryItemSchema = z.object({
  id: z.string().uuid(),
  orderId: z.string().uuid(),
  assetId: z.string().uuid(),
  assetTitle: z.string().min(1),
  price: z.number().nonnegative(),
  purchasedAt: isoDateTimeSchema,
  authorUsername: z.string().min(1),
  hasUserReviewed: z.boolean(),
  purchasedVersionNumber: z.number().int().positive(),
  purchasedVersionId: z.string().uuid(),
  latestEntitledVersionNumber: z.number().int().positive(),
  latestEntitledVersionId: z.string().uuid(),
  hasUpdate: z.boolean(),
  pricePaid: z.number().nonnegative(),
  currency: z.string().min(1),
  source: purchaseSourceSchema,
  bundleId: z.string().uuid().nullable().optional().default(null),
  bundleTitle: z.string().nullable().optional().default(null),
})

export type PurchaseLibraryItem = z.infer<typeof purchaseLibraryItemSchema>

export const pagedPurchaseLibraryResponseSchema = z.object({
  items: z.array(purchaseLibraryItemSchema),
  totalCount: z.number().int().nonnegative(),
  page: z.number().int().positive(),
  pageSize: z.number().int().nonnegative(),
})

export type PagedPurchaseLibraryDto = z.infer<typeof pagedPurchaseLibraryResponseSchema>

export const ASSET_VERSION_PROCESSING_STATUSES = [
  'PENDING_INSPECTION',
  'PENDING_MALWARE_SCAN',
  'READY',
  'REJECTED',
  'PROCESSING_FAILED',
] as const

export const assetVersionProcessingStatusSchema = z.enum(ASSET_VERSION_PROCESSING_STATUSES)
export type AssetVersionProcessingStatus = z.infer<typeof assetVersionProcessingStatusSchema>

export const assetVersionSummarySchema = z.object({
  id: z.string().uuid(),
  versionNumber: z.number().int().positive(),
  isCurrent: z.boolean(),
  fileName: z.string().min(1),
  contentLength: z.number().int().nonnegative(),
  contentSha256: z.string().min(1),
  releaseNotes: z.string().nullable().optional().default(''),
  createdAt: isoDateTimeSchema,
  license: assetLicenseSummarySchema,
  processingStatus: assetVersionProcessingStatusSchema.optional().default('READY'),
  processingErrorCode: z.string().nullable().optional().default(null),
  processingErrorSummary: z.string().nullable().optional().default(null),
  processingUpdatedAt: isoDateTimeSchema.nullable().optional().default(null),
})

export type AssetVersionSummary = z.infer<typeof assetVersionSummarySchema>
export type { AssetLicenseCode, AssetLicenseSummaryApi }
export { assetLicenseCodeSchema, assetLicenseSummarySchema }

export const assetVersionsResponseSchema = z.array(assetVersionSummarySchema)
