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

const bodySchema = z.object({
  email: z.string().min(1, 'Email is required').email('Enter a valid email address').max(256),
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

  const emailKey = parsed.data.email.trim().toLowerCase()
  const ip = getVerifiedClientIp(request)
  const rateLimited =
    enforceBffRateLimit(`password-reset-request:ip:${ip}`, 5, 60_000) ??
    enforceBffRateLimit(`password-reset-request:email:${emailKey}`, 5, 60_000)
  if (rateLimited) return rateLimited

  const base = getServerApiBaseUrl()
  let res: Response
  try {
    res = await fetch(`${base}/api/auth/password-reset/request`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: parsed.data.email }),
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
