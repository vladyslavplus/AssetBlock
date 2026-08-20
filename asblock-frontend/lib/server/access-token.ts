const ACCESS_TOKEN_LEEWAY_SECONDS = 30

/** Returns true when a JWT access token is missing, malformed, or past its exp claim. */
export function isAccessTokenExpired(token: string): boolean {
  try {
    const parts = token.split('.')
    if (parts.length < 2) {
      return true
    }

    const payloadSegment = parts[1].replace(/-/g, '+').replace(/_/g, '/')
    const padded = payloadSegment.padEnd(Math.ceil(payloadSegment.length / 4) * 4, '=')
    const payload = JSON.parse(Buffer.from(padded, 'base64').toString('utf8')) as { exp?: number }
    if (typeof payload.exp !== 'number') {
      return true
    }

    return payload.exp * 1000 <= Date.now() + ACCESS_TOKEN_LEEWAY_SECONDS * 1000
  } catch {
    return true
  }
}
