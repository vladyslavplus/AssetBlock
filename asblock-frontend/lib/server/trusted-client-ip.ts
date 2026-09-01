import 'server-only'

import { isIP } from 'node:net'
import { getServerEnvironment } from '@/lib/env'

/**
 * Resolves a trusted client IP for analytics BFF rate-limit partitioning.
 * Never uses `request.ip`. Never logs raw IP or header values.
 */
export function resolveTrustedClientIp(request: Request): string | null {
  const configuredHeader = getServerEnvironment().trustedClientIpHeader
  if (configuredHeader) {
    return parseAndValidateIpHeader(request.headers.get(configuredHeader))
  }

  if (process.env.VERCEL === '1') {
    return parseAndValidateIpHeader(request.headers.get('x-vercel-forwarded-for'))
  }

  if (process.env.NODE_ENV !== 'production') {
    return '127.0.0.1'
  }

  return null
}

function parseAndValidateIpHeader(raw: string | null): string | null {
  if (!raw) {
    return null
  }

  const candidate = raw.split(',')[0]?.trim()
  if (!candidate) {
    return null
  }

  return isIP(candidate) === 0 ? null : candidate
}
