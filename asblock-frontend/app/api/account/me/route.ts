import { NextResponse } from 'next/server'
import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'
import { accountProfileUpdateSchema } from '@/lib/account/account-schemas'

export async function GET() {
  const store = await cookies()
  const res = await fetchBackendAuthorized(store, '/api/users/me', { method: 'GET' })
  return forwardBackendResponse(res)
}

export async function PATCH(request: Request) {
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

  const parsed = accountProfileUpdateSchema.safeParse(bodyJson)
  if (!parsed.success) {
    return NextResponse.json(
      {
        error: 'ERR_VALIDATION',
        message: 'Invalid profile payload',
        details: parsed.error.format(),
      },
      { status: 400 },
    )
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(store, '/api/users/me', {
    method: 'PATCH',
    body: JSON.stringify(parsed.data),
    headers: { 'Content-Type': 'application/json' },
  })
  return forwardBackendResponse(res)
}
