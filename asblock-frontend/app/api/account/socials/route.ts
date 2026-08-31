import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardAuthenticatedBackendResponse,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
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

  const store = await cookies()
  const res = await fetchBackendAuthorized(store, '/api/users/me/socials', {
    method: 'PUT',
    body: JSON.stringify(parsed.data),
    headers: { 'Content-Type': 'application/json' },
    signal: request.signal,
  })
  return forwardAuthenticatedBackendResponse(res)
}
