import { describe, expect, it } from 'vitest'
import { leaveReviewFormSchema } from './review-schemas'

describe('leaveReviewFormSchema', () => {
  it('shows the rating message for 0 and the max message for 6', () => {
    const zero = leaveReviewFormSchema.safeParse({ rating: 0, comment: '' })
    const six = leaveReviewFormSchema.safeParse({ rating: 6, comment: '' })

    expect(zero.error?.issues[0]?.message).toBe('Choose a rating')
    expect(six.error?.issues[0]?.message).toBe('Number must be less than or equal to 5')
  })
})
