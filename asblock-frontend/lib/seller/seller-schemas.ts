import { z } from "zod";

const MAX_BYTES = 100 * 1024 * 1024;

const ASSET_DESCRIPTION_MAX = 5000;

export const assetUploadFormSchema = z.object({
  title: z.string().min(1, "Title is required").max(500),
  description: z
    .string()
    .max(ASSET_DESCRIPTION_MAX, `Description must be at most ${ASSET_DESCRIPTION_MAX} characters`)
    .optional(),
  price: z.coerce.number().positive("Price must be greater than zero"),
  categoryId: z.string().uuid("Select a category"),
  tags: z.string().optional(),
  file: z
    .custom<File>((val) => val instanceof File && val.size > 0, "Choose a file to upload")
    .refine((f) => !(f instanceof File) || f.size <= MAX_BYTES, "File must be at most 100 MB"),
});

export type AssetUploadFormValues = z.infer<typeof assetUploadFormSchema>;

export const assetEditFormSchema = z.object({
  title: z.string().min(1, "Title is required").max(500),
  description: z
    .string()
    .max(ASSET_DESCRIPTION_MAX, `Description must be at most ${ASSET_DESCRIPTION_MAX} characters`)
    .optional(),
  price: z.coerce.number().positive("Price must be greater than zero"),
  categoryId: z.string().uuid("Select a category"),
  tags: z.string().optional(),
});

export type AssetEditFormValues = z.infer<typeof assetEditFormSchema>;
