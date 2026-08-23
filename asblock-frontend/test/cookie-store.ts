import type { AuthCookieStore } from '@/lib/server/auth-cookies'

interface StoredCookie {
  value: string
  options?: Record<string, unknown>
}

/** In-memory Next.js cookie store for BFF helper tests. */
export function createMemoryCookieStore(initial: Record<string, string> = {}): AuthCookieStore & {
  snapshot: () => Record<string, string>
  setCalls: Array<{ name: string; value: string; options?: Record<string, unknown> }>
} {
  const map = new Map<string, StoredCookie>()
  const setCalls: Array<{ name: string; value: string; options?: Record<string, unknown> }> = []

  for (const [name, value] of Object.entries(initial)) {
    map.set(name, { value })
  }

  const store = {
    setCalls,
    snapshot: () =>
      Object.fromEntries([...map.entries()].map(([name, entry]) => [name, entry.value])),
    get: (name: string) => {
      const entry = map.get(name)
      return entry ? { name, value: entry.value } : undefined
    },
    set: (name: string, value: string, options?: Record<string, unknown>) => {
      map.set(name, { value, options })
      setCalls.push({ name, value, options })
    },
    delete: (name: string) => {
      map.delete(name)
    },
  }

  return store as unknown as AuthCookieStore & {
    snapshot: () => Record<string, string>
    setCalls: typeof setCalls
  }
}

export function makeJwt(expUnixSeconds: number): string {
  const payload = Buffer.from(JSON.stringify({ exp: expUnixSeconds }), 'utf8').toString('base64url')
  return `eyJhbGciOiJub25lIn0.${payload}.sig`
}
