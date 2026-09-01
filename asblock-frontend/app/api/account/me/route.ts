import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { accountProfileUpdateSchema } from '@/lib/account/account-schemas'

export async function GET(request: Request) {
  return proxyAuthenticatedBff(request, { path: '/api/users/me', init: { method: 'GET' } })
}

export async function PATCH(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const bodyText = await request.text()
  let bodyJson: unknown
  try {
    bodyJson = JSON.parse(bodyText)
  } catch {
    return invalidJsonResponse()
  }

  const parsed = accountProfileUpdateSchema.safeParse(bodyJson)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  return proxyAuthenticatedBff(request, {
    path: '/api/users/me',
    init: {
      method: 'PATCH',
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}
