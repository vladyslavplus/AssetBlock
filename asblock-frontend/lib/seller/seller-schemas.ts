import { z } from 'zod'

export const ASSET_UPLOAD_MAX_BYTES = 250 * 1024 * 1024
export const ASSET_UPLOAD_ALLOWED_EXTENSIONS = ['.zip', '.7z', '.rar', '.tar', '.tar.gz', '.tgz']

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
  price: z.coerce.number().positive('Price must be greater than zero'),
  categoryId: z.string().uuid('Select a category'),
  tags: z.string().optional(),
  file: z
    .custom<File>((val) => val instanceof File && val.size > 0, 'Choose a file to upload')
    .refine(
      (file) => !(file instanceof File) || file.size <= ASSET_UPLOAD_MAX_BYTES,
      'File must be at most 250 MiB',
    )
    .refine(
      (file) => !(file instanceof File) || hasAllowedArchiveExtension(file),
      'Choose a .zip, .7z, .rar, .tar, .tar.gz, or .tgz archive',
    ),
})

export type AssetUploadFormValues = z.infer<typeof assetUploadFormSchema>

export const assetEditFormSchema = z.object({
  title: z.string().min(1, 'Title is required').max(500),
  description: z
    .string()
    .max(ASSET_DESCRIPTION_MAX, `Description must be at most ${ASSET_DESCRIPTION_MAX} characters`)
    .optional(),
  price: z.coerce.number().positive('Price must be greater than zero'),
  categoryId: z.string().uuid('Select a category'),
  tags: z.string().optional(),
})

export type AssetEditFormValues = z.infer<typeof assetEditFormSchema>
