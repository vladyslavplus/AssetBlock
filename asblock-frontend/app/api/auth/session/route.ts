import { cookies } from 'next/headers'
import { NextResponse } from 'next/server'
import { fetchBackend } from '@/lib/server/fetch-backend'

export async function GET() {
  const store = await cookies()
  const res = await fetchBackend(store, '/api/users/me', { method: 'GET' }, 'optional')

  if (!res.ok) {
    return NextResponse.json({ user: null })
  }

  const user: unknown = await res.json().catch(() => null)
  return NextResponse.json({ user })
}
