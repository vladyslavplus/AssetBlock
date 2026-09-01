import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { adminCategoryUpdateSchema } from '@/lib/admin/admin-schemas'

export async function PUT(request: Request, context: { params: Promise<{ id: string }> }) {
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

  const parsed = adminCategoryUpdateSchema.safeParse(bodyJson)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/categories/${encodeURIComponent(parsedId.value)}`,
    init: {
      method: 'PUT',
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}

export async function DELETE(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/categories/${encodeURIComponent(parsedId.value)}`,
    init: { method: 'DELETE' },
    enforceSameOrigin: true,
  })
}
