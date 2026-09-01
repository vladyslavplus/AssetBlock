import { z } from 'zod'
import {
  assertSameOrigin,
  invalidJsonResponse,
  problemResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

const bodySchema = z.object({
  currentPassword: z.string().min(1, 'Current password is required'),
  newPassword: z.string().min(8, 'New password must be at least 8 characters'),
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

  const { currentPassword, newPassword } = parsed.data
  if (currentPassword === newPassword) {
    return problemResponse(
      400,
      'ERR_VALIDATION_FAILED',
      'One or more validation errors occurred.',
      { newPassword: ['New password must differ from the current one.'] },
    )
  }

  return proxyAuthenticatedBff(request, {
    path: '/api/users/me/password',
    init: {
      method: 'POST',
      body: JSON.stringify({ currentPassword, newPassword }),
    },
  })
}
