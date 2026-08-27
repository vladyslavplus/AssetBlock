import { NextResponse } from 'next/server'
import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'
import { leaveReviewFormSchema } from '@/lib/reviews/review-schemas'

export async function POST(request: Request, context: { params: Promise<{ assetId: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const bodyText = await request.text()
  let bodyJson: unknown
  try {
    bodyJson = JSON.parse(bodyText)
  } catch {
    return NextResponse.json(
      { error: 'ERR_VALIDATION', message: 'Invalid JSON payload' },
      { status: 400 },
    )
  }

  const parsed = leaveReviewFormSchema.safeParse(bodyJson)
  if (!parsed.success) {
    return NextResponse.json(
      {
        error: 'ERR_VALIDATION',
        message: 'Invalid review payload',
        details: parsed.error.format(),
      },
      { status: 400 },
    )
  }

  const { assetId } = await context.params
  const store = await cookies()
  const path = `/api/reviews/assets/${encodeURIComponent(assetId)}/reviews`
  const res = await fetchBackendAuthorized(store, path, {
    method: 'POST',
    body: JSON.stringify(parsed.data),
    headers: { 'Content-Type': 'application/json' },
  })
  return forwardBackendResponse(res)
}
