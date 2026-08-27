import { NextResponse } from 'next/server'
import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'
import { adminCategoryCreateSchema } from '@/lib/admin/admin-schemas'

export async function POST(request: Request) {
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

  const parsed = adminCategoryCreateSchema.safeParse(bodyJson)
  if (!parsed.success) {
    return NextResponse.json(
      {
        error: 'ERR_VALIDATION',
        message: 'Invalid category payload',
        details: parsed.error.format(),
      },
      { status: 400 },
    )
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(store, '/api/categories', {
    method: 'POST',
    body: JSON.stringify(parsed.data),
    headers: { 'Content-Type': 'application/json' },
  })
  return forwardBackendResponse(res)
}
