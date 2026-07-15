import { z } from 'zod'

const slugSchema = z
  .string()
  .min(1, 'Required')
  .max(200)
  .regex(/^[a-z0-9]+(?:-[a-z0-9]+)*$/i, 'Use letters, numbers, and single hyphens')

export const adminCategoryCreateSchema = z.object({
  name: z.string().min(1, 'Name required').max(200),
  description: z.string().max(4000).optional().nullable(),
  slug: slugSchema,
})

export const adminCategoryUpdateSchema = z.object({
  name: z.string().min(1, 'Name required').max(200),
  description: z.string().max(4000).optional().nullable(),
  slug: slugSchema,
})

export const adminTagCreateSchema = z.object({
  name: z.string().min(1, 'Name required').max(200).trim(),
})

export const adminTagUpdateSchema = z.object({
  name: z.string().min(1).max(200).trim(),
})

export const adminReviewDeleteSchema = z.object({
  reviewId: z.string().uuid('Enter a valid review ID'),
})

export type AdminCategoryCreateInput = z.infer<typeof adminCategoryCreateSchema>
export type AdminCategoryUpdateInput = z.infer<typeof adminCategoryUpdateSchema>
export type AdminTagCreateInput = z.infer<typeof adminTagCreateSchema>
export type AdminTagUpdateInput = z.infer<typeof adminTagUpdateSchema>
