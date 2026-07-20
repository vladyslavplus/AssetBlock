import { z } from 'zod'
import { ASSET_LICENSE_CODES } from '@/lib/assets/license-types'
import {
  ASSET_UPLOAD_ALLOWED_EXTENSIONS,
  ASSET_UPLOAD_MAX_BYTES,
} from '@/lib/seller/seller-schemas'

const RELEASE_NOTES_MAX = 4000

export const licenseCodeFieldSchema = z.enum(ASSET_LICENSE_CODES)

export const assetUploadMultipartSchema = z.object({
  title: z.string().trim().min(1, 'Title is required').max(500),
  description: z.string().max(5000, 'Description must be at most 5000 characters').optional(),
  price: z.coerce.number().positive('Price must be greater than zero'),
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

export function validateArchiveUploadFile(file: File | null): string | null {
  if (!file || file.size <= 0) {
    return 'Choose a file to upload.'
  }
  if (file.size > ASSET_UPLOAD_MAX_BYTES) {
    return 'File must be at most 250 MiB.'
  }
  if (!hasAllowedArchiveExtension(file.name)) {
    return 'Choose a .zip, .7z, .rar, .tar, .tar.gz, or .tgz archive.'
  }
  return null
}

function readOptionalString(formData: FormData, key: string): string | undefined {
  const raw = formData.get(key)
  if (typeof raw !== 'string') return undefined
  const trimmed = raw.trim()
  return trimmed.length > 0 ? trimmed : undefined
}

export function parseAssetUploadMultipart(formData: FormData) {
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

  const fileEntry = formData.get('file')
  const file = fileEntry instanceof File ? fileEntry : null
  const fileError = validateArchiveUploadFile(file)

  return { parsed, file, fileError }
}

export function parsePublishVersionMultipart(formData: FormData) {
  const parsed = publishVersionMultipartSchema.safeParse({
    licenseCode: readOptionalString(formData, 'licenseCode') ?? '',
    releaseNotes: readOptionalString(formData, 'releaseNotes') ?? '',
  })

  const fileEntry = formData.get('file')
  const file = fileEntry instanceof File ? fileEntry : null
  const fileError = validateArchiveUploadFile(file)

  return { parsed, file, fileError }
}

export function buildAssetUploadForwardForm(
  values: z.infer<typeof assetUploadMultipartSchema>,
  file: File,
): FormData {
  const fd = new FormData()
  fd.set('title', values.title)
  if (values.description) fd.set('description', values.description)
  fd.set('price', String(values.price))
  fd.set('categoryId', values.categoryId)
  fd.set('licenseCode', values.licenseCode)
  fd.set('file', file)
  for (const tag of values.tags ?? []) {
    fd.append('tags', tag)
  }
  return fd
}

export function buildPublishVersionForwardForm(
  values: z.infer<typeof publishVersionMultipartSchema>,
  file: File,
): FormData {
  const fd = new FormData()
  fd.set('licenseCode', values.licenseCode)
  fd.set('releaseNotes', values.releaseNotes)
  fd.set('file', file)
  return fd
}
