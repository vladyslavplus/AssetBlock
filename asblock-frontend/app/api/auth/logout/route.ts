import { cookies } from 'next/headers'
import { NextResponse } from 'next/server'
import { clearAuthCookies } from '@/lib/server/auth-cookies'
import { assertSameOrigin } from '@/lib/server/bff-http'

export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const store = await cookies()
  clearAuthCookies(store)
  return NextResponse.json({ ok: true })
}
