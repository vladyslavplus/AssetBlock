import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardAuthenticatedBackendResponse } from '@/lib/server/bff-http'

export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const store = await cookies()
  const res = await fetchBackendAuthorized(store, '/api/users/me/email-verification/resend', {
    method: 'POST',
    signal: request.signal,
  })

  return forwardAuthenticatedBackendResponse(res)
}
