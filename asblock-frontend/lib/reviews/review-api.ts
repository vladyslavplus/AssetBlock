import { getApiErrorMessage } from '@/lib/http/api-errors'
import type { LeaveReviewFormValues } from '@/lib/reviews/review-schemas'

export class ReviewRequestError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ReviewRequestError'
    this.status = status
  }
}

export async function postAssetReview(
  assetId: string,
  values: LeaveReviewFormValues,
): Promise<void> {
  const commentTrim = values.comment.trim()
  const res = await fetch(`/api/reviews/assets/${encodeURIComponent(assetId)}/reviews`, {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      rating: values.rating,
      comment: commentTrim.length > 0 ? commentTrim : null,
    }),
  })
  const text = await res.text()
  let json: unknown = null
  if (text.length > 0) {
    try {
      json = JSON.parse(text) as unknown
    } catch {
      json = text
    }
  }
  if (res.status === 401) {
    throw new ReviewRequestError(401, 'Sign in again to leave a review.')
  }
  if (!res.ok) {
    throw new ReviewRequestError(res.status, getApiErrorMessage(json, 'Could not submit review.'))
  }
}
