import { getServerApiBaseUrl } from '@/lib/http/api-config'
import { readApiResponseBody } from '@/lib/http/api-errors'
import { DEFAULT_BACKEND_TIMEOUT_MS } from '@/lib/server/fetch-backend'
import { transportErrorBody } from '@/lib/server/transport-error-body'

type AuthAction = 'login' | 'register' | 'refresh' | 'logout'

export interface PostAuthJsonOptions {
  signal?: AbortSignal
  timeoutMs?: number
}

/**
 * POST JSON to `/api/auth/{action}` on the AssetBlock Web API from the Next.js server.
 * Network/TLS failures return 502, timeouts return 504 with structured ProblemDetails body.
 */
export async function postAuthJson(
  action: AuthAction,
  body: unknown,
  options?: PostAuthJsonOptions,
): Promise<{ ok: boolean; status: number; data: unknown; headers?: Headers }> {
  const base = getServerApiBaseUrl()
  const timeoutMs = options?.timeoutMs ?? DEFAULT_BACKEND_TIMEOUT_MS

  const controller = new AbortController()
  let timedOut = false
  const timer = setTimeout(() => {
    timedOut = true
    controller.abort('timeout')
  }, timeoutMs)

  const onCallerAbort = () => {
    controller.abort(options?.signal?.reason)
  }

  if (options?.signal) {
    if (options.signal.aborted) {
      clearTimeout(timer)
      controller.abort(options.signal.reason)
    } else {
      options.signal.addEventListener('abort', onCallerAbort, { once: true })
    }
  }

  let res: Response
  try {
    res = await fetch(`${base}/api/auth/${action}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      cache: 'no-store',
      signal: controller.signal,
    })
  } catch (e: unknown) {
    if (timedOut || controller.signal.reason === 'timeout') {
      return {
        ok: false,
        status: 504,
        data: {
          type: 'urn:assetblock:error:ERR_GATEWAY_TIMEOUT',
          title: 'Request timeout',
          status: 504,
          detail: 'The authentication request timed out.',
          code: 'ERR_GATEWAY_TIMEOUT',
        },
      }
    }

    return { ok: false, status: 502, data: transportErrorBody(e) }
  } finally {
    clearTimeout(timer)
    if (options?.signal) {
      options.signal.removeEventListener('abort', onCallerAbort)
    }
  }

  const data = await readApiResponseBody(res)
  return { ok: res.ok, status: res.status, data, headers: res.headers }
}
