import { z } from 'zod'
import { getServerApiBaseUrl } from '@/lib/http/api-config'
import { readApiResponseBody } from '@/lib/http/api-errors'
import { transportErrorBody } from '@/lib/server/transport-error-body'
import {
  assertSameOrigin,
  forwardBackendResponse,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { enforceBffRateLimit, getVerifiedClientIp } from '@/lib/server/bff-rate-limit'

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

  const base = getServerApiBaseUrl()
  let res: Response
  try {
    res = await fetch(`${base}/api/auth/email-change/confirm`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: parsed.data.token }),
      cache: 'no-store',
    })
  } catch (e) {
    const body = transportErrorBody(e)
    return new Response(JSON.stringify(body), {
      status: 502,
      headers: { 'Content-Type': 'application/problem+json' },
    })
  }

  if (!res.ok) {
    const data = await readApiResponseBody(res)
    return new Response(JSON.stringify(data), {
      status: res.status,
      headers: { 'Content-Type': 'application/problem+json' },
    })
  }

  return forwardBackendResponse(res)
}
