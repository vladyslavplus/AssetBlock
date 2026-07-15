import type { NextRequest } from 'next/server'
import { NextResponse } from 'next/server'
import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { isAdminRole } from '@/lib/auth/roles'
import { getRoutePolicy } from '@/lib/auth/route-policy'

const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role'

function readAccessTokenRole(token: string | undefined): string | null {
  if (!token) return null
  const encodedPayload = token.split('.')[1]
  if (!encodedPayload) return null

  try {
    const base64 = encodedPayload.replace(/-/g, '+').replace(/_/g, '/')
    const padded = base64.padEnd(Math.ceil(base64.length / 4) * 4, '=')
    const payload: unknown = JSON.parse(atob(padded))
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) return null
    const claims = payload as Record<string, unknown>
    const role = claims.role ?? claims[ROLE_CLAIM]
    return typeof role === 'string' ? role : null
  } catch {
    return null
  }
}

export function proxy(request: NextRequest) {
  const hasRefresh = Boolean(request.cookies.get(AUTH_COOKIE_REFRESH)?.value)
  const { pathname } = request.nextUrl

  const policy = getRoutePolicy(pathname)

  if (policy?.sessionRequired && !hasRefresh) {
    const url = request.nextUrl.clone()
    url.pathname = '/login'
    url.searchParams.set('returnUrl', `${pathname}${request.nextUrl.search}`)
    return NextResponse.redirect(url)
  }

  // Coarse UX guard only. AdminLayout and the backend remain authoritative.
  if (
    policy?.role &&
    isAdminRole(readAccessTokenRole(request.cookies.get(AUTH_COOKIE_ACCESS)?.value))
  ) {
    return NextResponse.next()
  }

  if (policy?.role && request.cookies.get(AUTH_COOKIE_ACCESS)?.value) {
    return NextResponse.redirect(new URL('/', request.url))
  }

  return NextResponse.next()
}

export const config = {
  matcher: [
    '/library',
    '/library/:path*',
    '/account',
    '/account/:path*',
    '/sell',
    '/sell/:path*',
    '/admin',
    '/admin/:path*',
  ],
}
