import { cookies } from 'next/headers'
import { NextResponse } from 'next/server'
import { AUTH_COOKIE_ACCESS } from '@/lib/auth/constants'
import { tryRefreshFromCookies } from '@/lib/server/refresh-session'

/**
 * Returns the current access JWT for SignalR WebSocket auth.
 * Browser cannot send Authorization header on WS; @microsoft/signalr passes this as access_token query (matches JwtBearer OnMessageReceived).
 *
 * SECURITY: exposes bearer to page JS — same XSS surface as storing token in memory. Keep CSP strict; use only for hub URL.
 */
export async function GET() {
  const store = await cookies()
  let access = store.get(AUTH_COOKIE_ACCESS)?.value ?? null
  if (!access) {
    const rotated = await tryRefreshFromCookies(store)
    access = rotated?.accessToken ?? null
  }
  if (!access) {
    return NextResponse.json(
      { errors: [{ identifier: 'auth', message: 'Unauthorized' }] },
      { status: 401 },
    )
  }
  return NextResponse.json(
    { accessToken: access },
    {
      headers: {
        'Cache-Control': 'no-store, no-cache, must-revalidate',
        Pragma: 'no-cache',
      },
    },
  )
}
