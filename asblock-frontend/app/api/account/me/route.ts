import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardBackendResponse,
  invalidJsonResponse,
} from '@/lib/server/bff-http'

export async function GET() {
  const store = await cookies()
  const res = await fetchBackendAuthorized(store, '/api/users/me', { method: 'GET' })
  return forwardBackendResponse(res)
}

export async function PATCH(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const store = await cookies()
  let body: string
  try {
    body = await request.text()
  } catch {
    return invalidJsonResponse()
  }
  const res = await fetchBackendAuthorized(store, '/api/users/me', {
    method: 'PATCH',
    body,
  })
  return forwardBackendResponse(res)
}
