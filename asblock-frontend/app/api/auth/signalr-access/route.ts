import { cookies } from 'next/headers'
import { clientClosedRequestResponse, problemResponse } from '@/lib/server/bff-http'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'

/**
 * Exchanges the session cookie for a short-lived hub-only JWT.
 * The BFF calls POST /api/auth/signalr-token on the backend (requires API bearer scheme).
 * Browser JS receives only the hub token — the session access JWT is never exposed.
 *
 * SECURITY: The hub token has a distinct audience and token_use=signalr claim;
 * it cannot authorize any REST API endpoint.
 */
export async function GET(request?: Request) {
  const store = await cookies()

  let res: Response
  try {
    res = await fetchBackendAuthorized(store, '/api/auth/signalr-token', {
      method: 'POST',
      signal: request?.signal,
    })
  } catch (_err: unknown) {
    if (request?.signal?.aborted) {
      return clientClosedRequestResponse()
    }
    return problemResponse(502, 'ERR_GATEWAY_ERROR', 'Could not reach the authentication service.')
  }

  if (!res.ok) {
    if (res.status === 499) {
      return clientClosedRequestResponse()
    }
    if (res.status === 401) {
      return problemResponse(401, 'ERR_UNAUTHORIZED', 'Unauthorized')
    }
    if (res.status === 429) {
      const retryAfter = res.headers.get('Retry-After')
      const headers: Record<string, string> = {
        'Cache-Control': 'no-store',
      }
      if (retryAfter) {
        headers['Retry-After'] = retryAfter
      }
      return problemResponse(
        429,
        'ERR_RATE_LIMIT_EXCEEDED',
        'Too many requests. Please try again later.',
        undefined,
        headers,
      )
    }
    if (res.status === 504) {
      return problemResponse(
        504,
        'ERR_GATEWAY_TIMEOUT',
        'The authentication service request timed out.',
      )
    }
    return problemResponse(502, 'ERR_GATEWAY_ERROR', 'Failed to obtain hub token.')
  }

  let data: { hubToken?: string; expiresAt?: string }
  try {
    data = await res.json()
  } catch {
    return problemResponse(
      502,
      'ERR_GATEWAY_ERROR',
      'Unexpected response from authentication service.',
    )
  }

  if (!data.hubToken) {
    return problemResponse(502, 'ERR_GATEWAY_ERROR', 'Hub token missing from response.')
  }

  return new Response(JSON.stringify({ hubToken: data.hubToken, expiresAt: data.expiresAt }), {
    status: 200,
    headers: {
      'Content-Type': 'application/json',
      'Cache-Control': 'no-store, no-cache, must-revalidate',
      Pragma: 'no-cache',
    },
  })
}
