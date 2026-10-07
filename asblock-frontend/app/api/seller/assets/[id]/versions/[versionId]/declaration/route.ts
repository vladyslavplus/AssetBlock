import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { sellerDeclarationSaveRequestSchema } from '@/lib/seller/seller-draft-schemas'

export async function GET(
  request: Request,
  context: { params: Promise<{ id: string; versionId: string }> },
) {
  const { id, versionId } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }
  const parsedVersionId = parseUuidParam('versionId', versionId)
  if (!parsedVersionId.ok) {
    return parsedVersionId.response
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/assets/${encodeURIComponent(parsedId.value)}/versions/${encodeURIComponent(parsedVersionId.value)}/declaration`,
    init: { method: 'GET' },
  })
}

export async function PUT(
  request: Request,
  context: { params: Promise<{ id: string; versionId: string }> },
) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id, versionId } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }
  const parsedVersionId = parseUuidParam('versionId', versionId)
  if (!parsedVersionId.ok) {
    return parsedVersionId.response
  }

  const bodyText = await request.text()
  let bodyJson: unknown
  try {
    bodyJson = JSON.parse(bodyText)
  } catch {
    return invalidJsonResponse()
  }

  const parsed = sellerDeclarationSaveRequestSchema.safeParse(bodyJson)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/assets/${encodeURIComponent(parsedId.value)}/versions/${encodeURIComponent(parsedVersionId.value)}/declaration`,
    init: {
      method: 'PUT',
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}
