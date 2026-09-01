import { z } from 'zod'
import {
  assertSameOrigin,
  forwardBackendProblem,
  forwardBackendResponse,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { enforceBffRateLimit, getVerifiedClientIp } from '@/lib/server/bff-rate-limit'
import { fetchBackendPublic } from '@/lib/server/fetch-backend'

const MAX_PROTECTED_TOKEN_LENGTH = 4096

const bodySchema = z.object({
  token: z
    .string()
    .min(1, 'Token is required')
    .max(MAX_PROTECTED_TOKEN_LENGTH, 'Token is too long'),
})

export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const rateLimited = enforceBffRateLimit(
    `email-action-confirm:${getVerifiedClientIp(request)}`,
    20,
    60_000,
  )
  if (rateLimited) return rateLimited

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

  const res = await fetchBackendPublic('/api/auth/email-verification/confirm', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token: parsed.data.token }),
    signal: request.signal,
  })

  return res.ok ? forwardBackendResponse(res) : forwardBackendProblem(res)
}
