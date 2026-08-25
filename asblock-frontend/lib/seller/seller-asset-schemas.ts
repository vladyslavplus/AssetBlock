import { z } from 'zod'

export const sellerProcessingStatusSchema = z.enum([
  'PENDING_INSPECTION',
  'PENDING_MALWARE_SCAN',
  'READY',
  'REJECTED',
  'PROCESSING_FAILED',
])
export type SellerProcessingStatus = z.infer<typeof sellerProcessingStatusSchema>

const isoDateTimeSchema = z.string().datetime({ offset: true })

const sellerProcessingFieldsSchema = {
  latestVersionId: z.string().uuid(),
  latestVersionNumber: z.number().int().nonnegative(),
  currentReadyVersionId: z.string().uuid().nullable(),
  latestProcessingStatus: sellerProcessingStatusSchema,
  latestProcessingUpdatedAt: isoDateTimeSchema,
  latestProcessingErrorCode: z.string().min(1).max(64).nullable(),
  latestProcessingErrorSummary: z.string().max(4000).nullable(),
}

export const sellerAssetListItemSchema = z
  .object({
    id: z.string().uuid(),
    title: z.string().min(1),
    description: z.string().nullable(),
    price: z.number(),
    categoryId: z.string().uuid(),
    categoryName: z.string().nullable(),
    authorId: z.string().uuid(),
    authorUsername: z.string(),
    createdAt: isoDateTimeSchema,
    tags: z.array(z.string()),
    averageRating: z.number(),
    ...sellerProcessingFieldsSchema,
  })
  .strict()
export type SellerAssetListItem = z.infer<typeof sellerAssetListItemSchema>

export const pagedSellerAssetListSchema = z.object({
  items: z.array(sellerAssetListItemSchema),
  totalCount: z.number().int().nonnegative(),
  page: z.number().int().positive(),
  pageSize: z.number().int().positive(),
  totalPages: z.number().int().nonnegative().optional(),
})
export type PagedSellerAssetList = z.infer<typeof pagedSellerAssetListSchema>

export const sellerAssetDetailSchema = z
  .object({
    id: z.string().uuid(),
    title: z.string().min(1),
    description: z.string().nullable(),
    price: z.number(),
    categoryId: z.string().uuid(),
    categoryName: z.string().nullable(),
    authorId: z.string().uuid(),
    authorUsername: z.string(),
    createdAt: isoDateTimeSchema,
    updatedAt: isoDateTimeSchema.nullable(),
    tags: z.array(z.string()),
    ...sellerProcessingFieldsSchema,
  })
  .strict()
export type SellerAssetDetail = z.infer<typeof sellerAssetDetailSchema>

export function isSellerListingPubliclyReady(
  item: Pick<SellerAssetListItem, 'currentReadyVersionId'>,
): boolean {
  return item.currentReadyVersionId != null
}
