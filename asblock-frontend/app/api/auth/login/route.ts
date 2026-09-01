import { cookies } from 'next/headers'
import { NextResponse } from 'next/server'
import { loginFormSchema } from '@/lib/auth/schemas'
import { tokensResponseSchema } from '@/lib/auth/tokens-schema'
import { postAuthJson } from '@/lib/server/auth-backend'
import { setAuthCookies } from '@/lib/server/auth-cookies'
import {
  enforceBffRateLimit,
  getVerifiedClientIp,
  hashBffRateLimitKey,
} from '@/lib/server/bff-rate-limit'
import {
  assertSameOrigin,
  invalidJsonResponse,
  problemResponse,
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

  const parsed = loginFormSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const normalizedEmail = parsed.data.email.trim().toLowerCase()
  const emailKey = await hashBffRateLimitKey(normalizedEmail)
  const rateLimited =
    enforceBffRateLimit(`auth-login:ip:${getVerifiedClientIp(request)}`, 10, 60_000) ??
    enforceBffRateLimit(`auth-login:email:${emailKey}`, 10, 60_000)
  if (rateLimited) return rateLimited

  const { ok, status, data, headers } = await postAuthJson('login', {
    email: parsed.data.email,
    password: parsed.data.password,
  })

  if (!ok) {
    return safeBackendProblemResponse(status, data, headers)
  }

  const tokens = tokensResponseSchema.safeParse(data)
  if (!tokens.success) {
    return problemResponse(
      502,
      'ERR_BAD_GATEWAY',
      'The authentication service returned an unexpected response.',
    )
  }

  const store = await cookies()
  setAuthCookies(store, tokens.data)
  return NextResponse.json({ ok: true })
}
