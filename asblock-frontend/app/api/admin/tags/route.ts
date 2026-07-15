import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'

export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const store = await cookies()
  const body = await request.text()
  const res = await fetchBackendAuthorized(store, '/api/tags', {
    method: 'POST',
    body,
  })
  return forwardBackendResponse(res)
}
