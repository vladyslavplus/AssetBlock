import { NextResponse } from 'next/server'
import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'
import { assetTagAddSchema } from '@/lib/seller/seller-schemas'

export async function POST(request: Request, context: { params: Promise<{ id: string }> }) {
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

  const parsed = assetTagAddSchema.safeParse(bodyJson)
  if (!parsed.success) {
    return NextResponse.json(
      { error: 'ERR_VALIDATION', message: 'Invalid tag payload', details: parsed.error.format() },
      { status: 400 },
    )
  }

  const { id } = await context.params
  const store = await cookies()
  const res = await fetchBackendAuthorized(store, `/api/assets/${encodeURIComponent(id)}/tags`, {
    method: 'POST',
    body: JSON.stringify(parsed.data),
    headers: { 'Content-Type': 'application/json' },
  })
  return forwardBackendResponse(res)
}
