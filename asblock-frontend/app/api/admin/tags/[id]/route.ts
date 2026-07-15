import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'

export async function PUT(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id } = await context.params
  const store = await cookies()
  const body = await request.text()
  const res = await fetchBackendAuthorized(store, `/api/tags/${encodeURIComponent(id)}`, {
    method: 'PUT',
    body,
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
