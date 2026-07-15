import { cookies } from 'next/headers'
import { NextResponse } from 'next/server'
import { loginFormSchema } from '@/lib/auth/schemas'
import { tokensResponseSchema } from '@/lib/auth/tokens-schema'
import { postAuthJson } from '@/lib/server/auth-backend'
import { setAuthCookies } from '@/lib/server/auth-cookies'
import {
  assertSameOrigin,
  invalidJsonResponse,
  problemResponse,
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

  const { ok, status, data } = await postAuthJson('login', {
    email: parsed.data.email,
    password: parsed.data.password,
  })

  if (!ok) {
    return new Response(JSON.stringify(data), {
      status,
      headers: { 'Content-Type': 'application/problem+json' },
    })
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
