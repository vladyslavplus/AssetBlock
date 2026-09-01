import { z } from 'zod'
import { ASSET_LICENSE_CODES } from '@/lib/assets/license-types'
import { marketplacePriceSchema } from '@/lib/marketplace/price-schema'
import { ASSET_UPLOAD_ALLOWED_EXTENSIONS } from '@/lib/seller/seller-schemas'

const RELEASE_NOTES_MAX = 4000

export const licenseCodeFieldSchema = z.enum(ASSET_LICENSE_CODES)

export const assetUploadMultipartSchema = z.object({
  title: z.string().trim().min(1, 'Title is required').max(500),
  description: z.string().max(5000, 'Description must be at most 5000 characters').optional(),
  price: marketplacePriceSchema,
  categoryId: z.string().uuid('Select a category'),
  licenseCode: licenseCodeFieldSchema,
  tags: z.array(z.string().trim().min(1)).optional(),
})

export const publishVersionMultipartSchema = z.object({
  licenseCode: licenseCodeFieldSchema,
  releaseNotes: z
    .string()
    .trim()
    .min(1, 'Release notes are required')
    .max(RELEASE_NOTES_MAX, `Release notes must be at most ${RELEASE_NOTES_MAX} characters`),
})

function hasAllowedArchiveExtension(fileName: string): boolean {
  const lower = fileName.toLowerCase()
  return ASSET_UPLOAD_ALLOWED_EXTENSIONS.some((ext) => lower.endsWith(ext))
}

export function validateArchiveUploadFilename(fileName: string | null): string | null {
  if (!fileName) {
    return 'Choose a file to upload.'
  }
  if (!hasAllowedArchiveExtension(fileName)) {
    return 'Choose a .zip, .tar, .tar.gz, or .tgz archive.'
  }
  return null
}

function readOptionalString(formData: FormData, key: string): string | undefined {
  const raw = formData.get(key)
  if (typeof raw !== 'string') return undefined
  const trimmed = raw.trim()
  return trimmed.length > 0 ? trimmed : undefined
}

export function parseAssetUploadMetadata(formData: FormData) {
  const tags = formData
    .getAll('tags')
    .filter((v): v is string => typeof v === 'string')
    .map((t) => t.trim())
    .filter(Boolean)

  const parsed = assetUploadMultipartSchema.safeParse({
    title: readOptionalString(formData, 'title') ?? '',
    description: readOptionalString(formData, 'description'),
    price: readOptionalString(formData, 'price'),
    categoryId: readOptionalString(formData, 'categoryId') ?? '',
    licenseCode: readOptionalString(formData, 'licenseCode') ?? '',
    tags: tags.length > 0 ? tags : undefined,
  })

  return parsed
}

export function parsePublishVersionMetadata(formData: FormData) {
  return publishVersionMultipartSchema.safeParse({
    licenseCode: readOptionalString(formData, 'licenseCode') ?? '',
    releaseNotes: readOptionalString(formData, 'releaseNotes') ?? '',
  })
}
