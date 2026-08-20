/**
 * Lightweight in-memory fixed-window rate limit for public BFF auth routes.
 *
 * Scope: local / single-instance Next.js only. Each process keeps its own Map, so
 * multi-instance or rolling-restart production must rate-limit at Redis, reverse
 * proxy, or WAF — do not rely on this Map for distributed enforcement.
 *
 * Memory bound: expired buckets are pruned; map size is capped at MAX_BUCKETS.
 * New keys under pressure share a per-policy overflow bucket
 * (`__overflow:${limit}:${windowMs}`) instead of growing forever.
 */
interface Bucket {
  count: number
  resetAt: number
}

const MAX_BUCKETS = 4096

const buckets = new Map<string, Bucket>()

let productionWarningLogged = false

function pruneExpiredBuckets(now: number): void {
  for (const [key, bucket] of buckets) {
    if (now >= bucket.resetAt) {
      buckets.delete(key)
    }
  }
}

function evictEarliestBucket(): void {
  let earliestKey: string | null = null
  let earliestReset = Number.POSITIVE_INFINITY
  for (const [key, bucket] of buckets) {
    if (bucket.resetAt < earliestReset) {
      earliestReset = bucket.resetAt
      earliestKey = key
    }
  }
  if (earliestKey) {
    buckets.delete(earliestKey)
  }
}

function tooManyRequests(retryAfterSec: number): Response {
  return new Response(
    JSON.stringify({
      type: 'https://httpstatuses.com/429',
      title: 'Too Many Requests',
      status: 429,
      detail: 'Rate limit exceeded. Try again later.',
      code: 'ERR_RATE_LIMITED',
    }),
    {
      status: 429,
      headers: {
        'Content-Type': 'application/problem+json',
        'Retry-After': String(retryAfterSec),
      },
    },
  )
}

function overflowKey(limit: number, windowMs: number): string {
  return `__overflow:${limit}:${windowMs}`
}

function getOrCreateBucket(key: string, now: number, limit: number, windowMs: number): Bucket {
  pruneExpiredBuckets(now)

  const existing = buckets.get(key)
  if (existing && now < existing.resetAt) {
    return existing
  }

  if (existing) {
    const refreshed: Bucket = { count: 0, resetAt: now + windowMs }
    buckets.set(key, refreshed)
    return refreshed
  }

  // New key — do not grow past MAX_BUCKETS; funnel extras into a per-policy overflow bucket.
  let storageKey = key
  if (buckets.size >= MAX_BUCKETS) {
    storageKey = overflowKey(limit, windowMs)
    const overflow = buckets.get(storageKey)
    if (overflow && now < overflow.resetAt) {
      return overflow
    }
    if (!buckets.has(storageKey) && buckets.size >= MAX_BUCKETS) {
      evictEarliestBucket()
    }
  }

  const created: Bucket = { count: 0, resetAt: now + windowMs }
  buckets.set(storageKey, created)
  return created
}

export function enforceBffRateLimit(key: string, limit: number, windowMs: number): Response | null {
  if (process.env.NODE_ENV === 'production' && !productionWarningLogged) {
    productionWarningLogged = true
    console.warn(
      '[bff-rate-limit] In-memory limiter is single-instance only. Use Redis, reverse proxy, or WAF rate limits in multi-instance production.',
    )
  }

  const now = Date.now()
  const bucket = getOrCreateBucket(key, now, limit, windowMs)
  bucket.count += 1

  if (bucket.count > limit) {
    const retryAfterSec = Math.max(1, Math.ceil((bucket.resetAt - now) / 1000))
    return tooManyRequests(retryAfterSec)
  }

  return null
}

/**
 * Client IP for BFF rate limiting.
 *
 * TRUSTED_CLIENT_IP_HEADER (e.g. `cf-connecting-ip`, `x-vercel-forwarded-for`):
 * only that exact header is read. Deployment invariant:
 * - A trusted edge/ingress MUST overwrite/strip any client-supplied value of that header.
 * - Next.js MUST NOT be reachable directly from the public internet while this is set.
 * - Do NOT set this to `x-forwarded-for` unless the same overwrite guarantee holds.
 *
 * Without TRUSTED_CLIENT_IP_HEADER, uses platform `request.ip` when present, else
 * a shared `unverified` bucket (safe default; does not trust client headers).
 */
export function getVerifiedClientIp(request: Request): string {
  const headerName = process.env.TRUSTED_CLIENT_IP_HEADER?.trim().toLowerCase()
  if (headerName) {
    const raw = request.headers.get(headerName)
    const value = raw?.split(',')[0]?.trim()
    if (value) return value
  }

  const maybeIp = (request as Request & { ip?: string }).ip
  if (typeof maybeIp === 'string' && maybeIp.length > 0) {
    return maybeIp
  }

  return 'unverified'
}
