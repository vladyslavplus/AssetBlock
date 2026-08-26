import { cookies } from 'next/headers'
import { NextResponse } from 'next/server'
import { AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { postAuthJson } from '@/lib/server/auth-backend'
import { clearAuthCookies } from '@/lib/server/auth-cookies'
import { assertSameOrigin } from '@/lib/server/bff-http'

export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const store = await cookies()
  const refreshToken = store.get(AUTH_COOKIE_REFRESH)?.value

  if (refreshToken) {
    try {
      await postAuthJson('logout', { refreshToken })
    } catch (error) {
      console.error('Backend logout failed', error)
    }
  }

  clearAuthCookies(store)
  return NextResponse.json({ ok: true })
}
