import { z } from 'zod'
import { marketplacePriceSchema } from '@/lib/marketplace/price-schema'

export const BUNDLE_MIN_ITEMS = 2
export const BUNDLE_MAX_ITEMS = 20
const TITLE_MAX = 160
const DESCRIPTION_MAX = 2000

export const createBundleSchema = z
  .object({
    title: z.string().trim().min(1, 'Title is required').max(TITLE_MAX),
    description: z
      .string()
      .trim()
      .max(DESCRIPTION_MAX, `Description must be at most ${DESCRIPTION_MAX} characters`)
      .optional()
      .nullable(),
    price: marketplacePriceSchema,
    assetIds: z
      .array(z.string().uuid('Asset ID must be a valid UUID.'))
      .min(BUNDLE_MIN_ITEMS, `Select at least ${BUNDLE_MIN_ITEMS} assets`)
      .max(BUNDLE_MAX_ITEMS, `Select at most ${BUNDLE_MAX_ITEMS} assets`),
  })
  .strict()
  .superRefine((data, ctx) => {
    if (new Set(data.assetIds).size !== data.assetIds.length) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: 'Duplicate assets are not allowed.',
        path: ['assetIds'],
      })
    }
  })

export type CreateBundleBody = z.infer<typeof createBundleSchema>

export const reviseBundleSchema = createBundleSchema
export type ReviseBundleBody = z.infer<typeof reviseBundleSchema>

export const bundleFormSchema = z
  .object({
    title: z.string().trim().min(1, 'Title is required').max(TITLE_MAX),
    description: z
      .string()
      .max(DESCRIPTION_MAX, `Description must be at most ${DESCRIPTION_MAX} characters`)
      .optional(),
    price: marketplacePriceSchema.optional(),
    assetIds: z
      .array(z.string().uuid())
      .min(BUNDLE_MIN_ITEMS, `Select at least ${BUNDLE_MIN_ITEMS} assets`)
      .max(BUNDLE_MAX_ITEMS, `Select at most ${BUNDLE_MAX_ITEMS} assets`),
  })
  .superRefine((data, ctx) => {
    if (data.price == null) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: 'Price must be greater than zero',
        path: ['price'],
      })
    }
  })

export type BundleFormValues = z.infer<typeof bundleFormSchema>

export const bundleItemResponseSchema = z.object({
  assetId: z.string().uuid().nullable(),
  title: z.string(),
  listPrice: z.number(),
  position: z.number().int().positive(),
  isAvailable: z.boolean(),
  unavailableReason: z.string().nullable(),
  currentVersionNumber: z.number().int().positive().nullable(),
  licenseCode: z.string().nullable(),
  licenseDisplayName: z.string().nullable(),
})

export const bundleListItemResponseSchema = z.object({
  id: z.string().uuid(),
  revisionId: z.string().uuid(),
  revisionNumber: z.number().int().positive(),
  title: z.string(),
  description: z.string().nullable(),
  price: z.number(),
  listPriceTotal: z.number(),
  savingsAmount: z.number(),
  savingsPercent: z.number(),
  currency: z.string(),
  itemCount: z.number().int().nonnegative(),
  sellerId: z.string().uuid(),
  sellerUsername: z.string(),
  createdAt: z.string(),
  isArchived: z.boolean(),
  isAvailable: z.boolean(),
})

export const bundleDetailResponseSchema = z.object({
  id: z.string().uuid(),
  revisionId: z.string().uuid(),
  revisionNumber: z.number().int().positive(),
  title: z.string(),
  description: z.string().nullable(),
  price: z.number(),
  listPriceTotal: z.number(),
  savingsAmount: z.number(),
  savingsPercent: z.number(),
  currency: z.string(),
  sellerId: z.string().uuid(),
  sellerUsername: z.string(),
  createdAt: z.string(),
  updatedAt: z.string().nullable(),
  archivedAt: z.string().nullable(),
  isArchived: z.boolean(),
  isAvailable: z.boolean(),
  items: z.array(bundleItemResponseSchema),
})

export const createBundleResponseSchema = z.object({
  id: z.string().uuid(),
  revisionId: z.string().uuid(),
  revisionNumber: z.number().int().positive(),
})

export const pagedBundlesResponseSchema = z.object({
  items: z.array(bundleListItemResponseSchema),
  totalCount: z.number().int().nonnegative(),
  page: z.number().int().positive(),
  pageSize: z.number().int().positive(),
})
