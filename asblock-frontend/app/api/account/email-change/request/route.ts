import { cookies } from 'next/headers'
import { z } from 'zod'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardBackendResponse,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'

const bodySchema = z.object({
  newEmail: z.string().min(1, 'Email is required').email('Enter a valid email address'),
  currentPassword: z.string().min(1, 'Current password is required'),
})

export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  let json: unknown
  try {
    json = await request.json()
  } catch {
    return invalidJsonResponse()
  }

  const parsed = bodySchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(store, '/api/users/me/email-change/request', {
    method: 'POST',
    body: JSON.stringify({
      newEmail: parsed.data.newEmail,
      currentPassword: parsed.data.currentPassword,
    }),
  })

  return forwardBackendResponse(res)
}
