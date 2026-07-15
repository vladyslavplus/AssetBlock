import { apiUrl } from '@/lib/http/api-config'
import { getApiErrorMessage, parseApiErrorBody, readApiResponseBody } from '@/lib/http/api-errors'

export class ApiRequestError extends Error {
  readonly status: number
  readonly body: unknown
  readonly code?: string
  readonly traceId?: string
  readonly fieldErrors: Record<string, string>

  constructor(message: string, status: number, body: unknown) {
    super(message)
    this.name = 'ApiRequestError'
    this.status = status
    this.body = body
    const parsed = parseApiErrorBody(body)
    this.code = parsed?.code
    this.traceId = parsed?.traceId
    this.fieldErrors = parsed?.fieldErrors ?? {}
  }
}

export interface ApiFetchOptions extends Omit<RequestInit, 'body'> {
  /** Relative to API host, e.g. `api/assets` or `/api/assets` */
  path: string
  /** JSON body; sets Content-Type and serializes. Mutually exclusive with `body` for raw FormData etc. */
  jsonBody?: unknown
  body?: BodyInit
}

/**
 * Fetch against AssetBlock Web API. Prefixes NEXT_PUBLIC_API_BASE_URL.
 * Throws {@link ApiRequestError} on non-OK responses after trying to parse JSON error body.
 */
export async function apiFetch<T = unknown>(options: ApiFetchOptions): Promise<T> {
  const { path, jsonBody, body: optionBody, headers: initHeaders, ...rest } = options
  const url = apiUrl(path)

  const headers = new Headers(initHeaders ?? undefined)
  let body: BodyInit | undefined = optionBody

  if (jsonBody !== undefined) {
    body = JSON.stringify(jsonBody)
    if (!headers.has('Content-Type')) {
      headers.set('Content-Type', 'application/json')
    }
  }

  const res = await fetch(url, {
    ...rest,
    headers,
    body,
  })

  const parsed = await readApiResponseBody(res)

  if (!res.ok) {
    const fallback =
      typeof parsed === 'string' && parsed.length > 0
        ? parsed
        : `Request failed: ${res.status} ${res.statusText}`
    const message = getApiErrorMessage(parsed, fallback)
    throw new ApiRequestError(message, res.status, parsed)
  }

  return parsed as T
}
