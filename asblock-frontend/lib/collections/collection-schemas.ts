import { z } from 'zod'

const TITLE_MAX = 160
const DESCRIPTION_MAX = 2000

export const createCollectionSchema = z
  .object({
    title: z.string().trim().min(1, 'Title is required').max(TITLE_MAX),
    description: z
      .string()
      .trim()
      .max(DESCRIPTION_MAX, `Description must be at most ${DESCRIPTION_MAX} characters`)
      .optional()
      .nullable(),
  })
  .strict()

export type CreateCollectionBody = z.infer<typeof createCollectionSchema>

export const updateCollectionSchema = createCollectionSchema
export type UpdateCollectionBody = z.infer<typeof updateCollectionSchema>

export const addCollectionItemSchema = z
  .object({
    assetId: z.string().uuid('Asset ID must be a valid UUID.'),
  })
  .strict()

export type AddCollectionItemBody = z.infer<typeof addCollectionItemSchema>

export const reorderCollectionItemsSchema = z
  .object({
    assetIds: z.array(z.string().uuid('Asset ID must be a valid UUID.')).max(50),
  })
  .strict()

export type ReorderCollectionItemsBody = z.infer<typeof reorderCollectionItemsSchema>

export const collectionMetadataFormSchema = z.object({
  title: z.string().trim().min(1, 'Title is required').max(TITLE_MAX),
  description: z
    .string()
    .max(DESCRIPTION_MAX, `Description must be at most ${DESCRIPTION_MAX} characters`)
    .optional(),
})

export type CollectionMetadataFormValues = z.infer<typeof collectionMetadataFormSchema>

export const collectionStatusResponseSchema = z.enum(['DRAFT', 'PUBLISHED', 'ARCHIVED'])
export type CollectionStatus = z.infer<typeof collectionStatusResponseSchema>

export const collectionItemResponseSchema = z.object({
  assetId: z.string().uuid(),
  title: z.string(),
  price: z.number(),
  position: z.number().int().positive(),
  isAvailable: z.boolean(),
  unavailableReason: z.string().nullable(),
})

export const collectionListItemResponseSchema = z.object({
  id: z.string().uuid(),
  title: z.string(),
  description: z.string().nullable(),
  status: collectionStatusResponseSchema,
  publishedAt: z.string().nullable(),
  createdAt: z.string(),
  sellerId: z.string().uuid(),
  sellerUsername: z.string(),
  itemCount: z.number().int().nonnegative(),
  coverAssetId: z.string().uuid().nullable(),
  coverAssetTitle: z.string().nullable(),
})

export const collectionDetailResponseSchema = z.object({
  id: z.string().uuid(),
  title: z.string(),
  description: z.string().nullable(),
  status: collectionStatusResponseSchema,
  publishedAt: z.string().nullable(),
  archivedAt: z.string().nullable(),
  createdAt: z.string(),
  updatedAt: z.string().nullable(),
  sellerId: z.string().uuid(),
  sellerUsername: z.string(),
  items: z.array(collectionItemResponseSchema),
})

export const createCollectionResponseSchema = z.object({
  id: z.string().uuid(),
})

export const pagedCollectionsResponseSchema = z.object({
  items: z.array(collectionListItemResponseSchema),
  totalCount: z.number().int().nonnegative(),
  page: z.number().int().positive(),
  pageSize: z.number().int().positive(),
})
