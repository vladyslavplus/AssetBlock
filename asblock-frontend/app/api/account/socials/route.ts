import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { updateSocialLinksSchema } from '@/lib/account/account-schemas'

export async function PUT(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const bodyText = await request.text()
  let bodyJson: unknown
  try {
    bodyJson = JSON.parse(bodyText)
  } catch {
    return invalidJsonResponse()
  }

  const parsed = updateSocialLinksSchema.safeParse(bodyJson)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  return proxyAuthenticatedBff(request, {
    path: '/api/users/me/socials',
    init: {
      method: 'PUT',
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}
