import { cookies } from 'next/headers'
import { NextResponse } from 'next/server'
import { AUTH_COOKIE_ACCESS } from '@/lib/auth/constants'
import { getServerApiBaseUrl } from '@/lib/http/api-config'
import { clearAuthCookies } from '@/lib/server/auth-cookies'
import { tryRefreshFromCookies } from '@/lib/server/refresh-session'

async function fetchMe(accessToken: string): Promise<Response | null> {
  const base = getServerApiBaseUrl()
  try {
    return await fetch(`${base}/api/users/me`, {
      headers: { Authorization: `Bearer ${accessToken}` },
      cache: 'no-store',
    })
  } catch {
    // TLS / unreachable API — fail soft so the UI stays anonymous instead of 500.
    return null
  }
}

export async function GET() {
  const store = await cookies()
  let access = store.get(AUTH_COOKIE_ACCESS)?.value ?? null

  if (!access) {
    const rotated = await tryRefreshFromCookies(store)
    access = rotated?.accessToken ?? null
  }

  if (!access) {
    return NextResponse.json({ user: null })
  }

  let me = await fetchMe(access)
  if (me === null) {
    return NextResponse.json({ user: null })
  }
  if (me.status === 401) {
    const rotated = await tryRefreshFromCookies(store)
    if (!rotated) {
      clearAuthCookies(store)
      return NextResponse.json({ user: null })
    }
    access = rotated.accessToken
    me = await fetchMe(access)
  }

  if (me === null || !me.ok) {
    return NextResponse.json({ user: null })
  }

  const user: unknown = await me.json().catch(() => null)
  return NextResponse.json({ user })
}
