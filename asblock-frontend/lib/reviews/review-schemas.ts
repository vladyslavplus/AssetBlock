import { z } from 'zod'

export const leaveReviewFormSchema = z.object({
  rating: z.number().int().min(1, 'Choose a rating').max(5),
  comment: z.string().max(1000, 'Comment must be at most 1000 characters'),
})

export type LeaveReviewFormValues = z.infer<typeof leaveReviewFormSchema>
