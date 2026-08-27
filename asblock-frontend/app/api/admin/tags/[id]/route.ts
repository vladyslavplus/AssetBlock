import { NextResponse } from 'next/server'
import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'
import { adminTagUpdateSchema } from '@/lib/admin/admin-schemas'

export async function PUT(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id } = await context.params
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

  const parsed = adminTagUpdateSchema.safeParse(bodyJson)
  if (!parsed.success) {
    return NextResponse.json(
      { error: 'ERR_VALIDATION', message: 'Invalid tag payload', details: parsed.error.format() },
      { status: 400 },
    )
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(store, `/api/tags/${encodeURIComponent(id)}`, {
    method: 'PUT',
    body: JSON.stringify(parsed.data),
    headers: { 'Content-Type': 'application/json' },
  })
  return forwardBackendResponse(res)
}

export async function DELETE(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id } = await context.params
  const store = await cookies()
  const res = await fetchBackendAuthorized(store, `/api/tags/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  })
  return forwardBackendResponse(res)
}
