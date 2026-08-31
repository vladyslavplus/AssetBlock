import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardAuthenticatedBackendResponse,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'
import { sellerAssetPatchSchema } from '@/lib/seller/seller-schemas'

export async function GET(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/users/me/assets/${encodeURIComponent(parsedId.value)}`,
    {
      method: 'GET',
      signal: request.signal,
    },
  )
  return forwardAuthenticatedBackendResponse(res)
}

export async function PATCH(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const bodyText = await request.text()
  let bodyJson: unknown
  try {
    bodyJson = JSON.parse(bodyText)
  } catch {
    return invalidJsonResponse()
  }

  const parsed = sellerAssetPatchSchema.safeParse(bodyJson)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/assets/${encodeURIComponent(parsedId.value)}`,
    {
      method: 'PATCH',
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
      signal: request.signal,
    },
  )
  return forwardAuthenticatedBackendResponse(res)
}

export async function DELETE(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/assets/${encodeURIComponent(parsedId.value)}`,
    {
      method: 'DELETE',
      signal: request.signal,
    },
  )
  return forwardAuthenticatedBackendResponse(res)
}
