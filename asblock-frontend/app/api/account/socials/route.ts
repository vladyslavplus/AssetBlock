import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'

export async function PUT(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const store = await cookies()
  const body = await request.text()
  const res = await fetchBackendAuthorized(store, '/api/users/me/socials', {
    method: 'PUT',
    body,
    headers: { 'Content-Type': 'application/json' },
  })
  return forwardBackendResponse(res)
}
