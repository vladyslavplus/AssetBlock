import { NextResponse } from 'next/server'
import { registerFormSchema } from '@/lib/auth/schemas'
import { postAuthJson } from '@/lib/server/auth-backend'
import {
  enforceBffRateLimit,
  getVerifiedClientIp,
  hashBffRateLimitKey,
} from '@/lib/server/bff-rate-limit'
import {
  assertSameOrigin,
  invalidJsonResponse,
  safeBackendProblemResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'

export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  let json: unknown
  try {
    json = await request.json()
  } catch {
    return invalidJsonResponse()
  }

  const parsed = registerFormSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const { username, email, password } = parsed.data
  const emailKey = await hashBffRateLimitKey(email.trim().toLowerCase())
  const rateLimited =
    enforceBffRateLimit(`auth-register:ip:${getVerifiedClientIp(request)}`, 5, 60_000) ??
    enforceBffRateLimit(`auth-register:email:${emailKey}`, 5, 60_000)
  if (rateLimited) return rateLimited

  const { ok, status, data, headers } = await postAuthJson('register', {
    username,
    email,
    password,
  })

  if (!ok) {
    return safeBackendProblemResponse(status, data, headers)
  }

  return NextResponse.json({ ok: true }, { status: 202 })
}
