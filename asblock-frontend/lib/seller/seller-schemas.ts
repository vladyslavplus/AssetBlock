import { z } from 'zod'
import { ASSET_LICENSE_CODES } from '@/lib/assets/license-types'
import { marketplacePriceSchema } from '@/lib/marketplace/price-schema'

export const ASSET_UPLOAD_MAX_BYTES = 250 * 1024 * 1024
export const ASSET_UPLOAD_ALLOWED_EXTENSIONS = ['.zip', '.tar', '.tar.gz', '.tgz']

function hasAllowedArchiveExtension(file: File): boolean {
  const fileName = file.name.toLowerCase()
  return ASSET_UPLOAD_ALLOWED_EXTENSIONS.some((extension) => fileName.endsWith(extension))
}

const ASSET_DESCRIPTION_MAX = 5000

export const assetUploadFormSchema = z.object({
  title: z.string().min(1, 'Title is required').max(500),
  description: z
    .string()
    .max(ASSET_DESCRIPTION_MAX, `Description must be at most ${ASSET_DESCRIPTION_MAX} characters`)
    .optional(),
  price: marketplacePriceSchema,
  categoryId: z.string().uuid('Select a category'),
  licenseCode: z.enum(ASSET_LICENSE_CODES, { required_error: 'Select a license' }),
  tags: z.string().optional(),
  file: z
    .custom<File>((val) => val instanceof File && val.size > 0, 'Choose a file to upload')
    .refine(
      (file) => !(file instanceof File) || file.size <= ASSET_UPLOAD_MAX_BYTES,
      'File must be at most 250 MiB',
    )
    .refine(
      (file) => !(file instanceof File) || hasAllowedArchiveExtension(file),
      'Choose a .zip, .tar, .tar.gz, or .tgz archive',
    ),
})

export type AssetUploadFormValues = z.infer<typeof assetUploadFormSchema>

export const assetEditFormSchema = z.object({
  title: z.string().min(1, 'Title is required').max(500),
  description: z
    .string()
    .max(ASSET_DESCRIPTION_MAX, `Description must be at most ${ASSET_DESCRIPTION_MAX} characters`)
    .optional(),
  price: marketplacePriceSchema,
  categoryId: z.string().uuid('Select a category'),
  tags: z.string().optional(),
})

export type AssetEditFormValues = z.infer<typeof assetEditFormSchema>

export const sellerAssetPatchSchema = z.object({
  title: z.string().min(1, 'Title is required').max(500).optional(),
  description: z
    .string()
    .max(ASSET_DESCRIPTION_MAX, `Description must be at most ${ASSET_DESCRIPTION_MAX} characters`)
    .nullable()
    .optional(),
  price: marketplacePriceSchema.optional(),
  categoryId: z.string().uuid('Select a category').optional(),
})

export type SellerAssetPatchValues = z.infer<typeof sellerAssetPatchSchema>

export const assetTagAddSchema = z.object({
  name: z
    .string()
    .trim()
    .min(1, 'Tag name is required')
    .max(50, 'Tag name must not exceed 50 characters'),
})

export type AssetTagAddValues = z.infer<typeof assetTagAddSchema>

const RELEASE_NOTES_MAX = 4000

export const publishVersionFormSchema = z.object({
  licenseCode: z.enum(ASSET_LICENSE_CODES, { required_error: 'Select a license' }),
  releaseNotes: z
    .string()
    .trim()
    .min(1, 'Release notes are required')
    .max(RELEASE_NOTES_MAX, `Release notes must be at most ${RELEASE_NOTES_MAX} characters`),
  file: z
    .custom<File>((val) => val instanceof File && val.size > 0, 'Choose a file to upload')
    .refine(
      (file) => !(file instanceof File) || file.size <= ASSET_UPLOAD_MAX_BYTES,
      'File must be at most 250 MiB',
    )
    .refine(
      (file) => !(file instanceof File) || hasAllowedArchiveExtension(file),
      'Choose a .zip, .tar, .tar.gz, or .tgz archive',
    ),
})

export type PublishVersionFormValues = z.infer<typeof publishVersionFormSchema>
