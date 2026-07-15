import { getServerApiBaseUrl } from '@/lib/http/api-config'
import { readApiResponseBody } from '@/lib/http/api-errors'
import { transportErrorBody } from '@/lib/server/transport-error-body'

type AuthAction = 'login' | 'register' | 'refresh'

/**
 * POST JSON to `/api/auth/{action}` on the AssetBlock Web API from the Next.js server.
 * Network/TLS failures return 502 with a structured `errors` body (same shape as the Web API).
 */
export async function postAuthJson(
  action: AuthAction,
  body: unknown,
): Promise<{ ok: boolean; status: number; data: unknown }> {
  const base = getServerApiBaseUrl()
  let res: Response
  try {
    res = await fetch(`${base}/api/auth/${action}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      cache: 'no-store',
    })
  } catch (e) {
    return { ok: false, status: 502, data: transportErrorBody(e) }
  }
  const data = await readApiResponseBody(res)
  return { ok: res.ok, status: res.status, data }
}
