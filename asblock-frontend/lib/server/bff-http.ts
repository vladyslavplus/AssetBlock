import 'server-only'
import type { ZodError } from 'zod'
import { parseApiErrorBody, readApiResponseBody } from '@/lib/http/api-errors'

const SAFE_BACKEND_RESPONSE_HEADERS = ['content-type', 'content-disposition'] as const
const BODYLESS_STATUSES = new Set([204, 205, 304])
const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS'])
const SAFE_PROBLEM_CODE = /^ERR_[A-Z0-9_]+$/

export function problemResponse(
  status: number,
  code: string,
  detail: string,
  errors?: Record<string, string[]>,
  extraHeaders?: Record<string, string>,
  titleOverride?: string,
): Response {
  const body = {
    type: `urn:assetblock:error:${code}`,
    status,
    title:
      titleOverride ??
      (status === 401 ? 'Unauthorized' : status === 403 ? 'Forbidden' : 'Request failed'),
    detail,
    code,
    traceId: crypto.randomUUID(),
    ...(errors ? { errors } : {}),
  }

  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': 'application/problem+json',
      ...(status === 401 || status === 403 ? { 'Cache-Control': 'no-store' } : {}),
      ...(extraHeaders ?? {}),
    },
  })
}

/** A browser disconnected before the Route Handler could return a response. */
export function clientClosedRequestResponse(): Response {
  return problemResponse(499, 'ERR_CLIENT_CLOSED_REQUEST', 'Client closed request.', undefined, {
    'Cache-Control': 'no-store',
  })
}

export function invalidJsonResponse(): Response {
  return problemResponse(400, 'ERR_VALIDATION_FAILED', 'The request body must be valid JSON.', {
    body: ['Invalid JSON body.'],
  })
}

export function zodValidationProblemResponse(error: ZodError): Response {
  const errors: Record<string, string[]> = {}
  for (const issue of error.issues) {
    const key = issue.path.join('.') || 'request'
    errors[key] = [...(errors[key] ?? []), issue.message]
  }
  return problemResponse(
    400,
    'ERR_VALIDATION_FAILED',
    'One or more validation errors occurred.',
    errors,
  )
}

/** Returns a 403 response when a state-changing BFF request is not same-origin. */
export function assertSameOrigin(request: Request): Response | null {
  if (SAFE_METHODS.has(request.method.toUpperCase())) return null
  if (request.headers.get('Sec-Fetch-Site')?.toLowerCase() === 'cross-site') {
    return problemResponse(403, 'ERR_ORIGIN_FORBIDDEN', 'Cross-site requests are not allowed.')
  }
  const origin = request.headers.get('Origin')
  const referer = request.headers.get('Referer')
  const browserSource = origin || referer
  if (!browserSource) {
    return problemResponse(403, 'ERR_ORIGIN_FORBIDDEN', 'A same-origin request is required.')
  }

  try {
    if (new URL(browserSource).origin !== new URL(request.url).origin) {
      return problemResponse(403, 'ERR_ORIGIN_FORBIDDEN', 'Cross-origin requests are not allowed.')
    }
  } catch {
    return problemResponse(
      403,
      'ERR_ORIGIN_FORBIDDEN',
      'The request origin information is invalid.',
    )
  }

  return null
}

function isSafeProblemText(value: string | undefined): value is string {
  return Boolean(
    value && value.length <= 300 && !/[<>\r\n]/.test(value) && !/\bat\s+\S+\s*\(/i.test(value),
  )
}

function readSafeBackendTitle(body: unknown): string | undefined {
  if (typeof body !== 'object' || body === null || Array.isArray(body)) return undefined
  const title = Reflect.get(body, 'title')
  return typeof title === 'string' && isSafeProblemText(title) ? title : undefined
}

/** Maps backend ProblemDetails to a bounded browser-safe contract and allowlisted headers. */
export function safeBackendProblemResponse(
  status: number,
  body: unknown,
  backendHeaders?: Headers,
): Response {
  const parsed = parseApiErrorBody(body)
  const clientErrorStatus = status >= 400 && status <= 499
  const code =
    clientErrorStatus && parsed?.code && SAFE_PROBLEM_CODE.test(parsed.code)
      ? parsed.code
      : 'ERR_BAD_GATEWAY'
  const detail =
    code !== 'ERR_BAD_GATEWAY' && isSafeProblemText(parsed?.summary)
      ? parsed.summary
      : 'The service returned an unexpected error response.'
  const retryAfter = backendHeaders?.get('Retry-After')
  const title = code !== 'ERR_BAD_GATEWAY' ? readSafeBackendTitle(body) : undefined
  return problemResponse(
    clientErrorStatus ? status : 502,
    code,
    detail,
    undefined,
    retryAfter ? { 'Retry-After': retryAfter } : undefined,
    title,
  )
}

export async function forwardBackendProblem(response: Response): Promise<Response> {
  return safeBackendProblemResponse(
    response.status,
    await readApiResponseBody(response),
    response.headers,
  )
}

/** Streams a backend response while forwarding only explicitly safe response headers. */
export function forwardBackendResponse(response: Response): Response {
  const headers = new Headers()
  for (const name of SAFE_BACKEND_RESPONSE_HEADERS) {
    const value = response.headers.get(name)
    if (value) {
      headers.set(name, value)
    }
  }

  return new Response(BODYLESS_STATUSES.has(response.status) ? null : response.body, {
    status: response.status,
    headers,
  })
}

/** Streams a download response; forwards Content-Type/Disposition and sets Cache-Control: no-store. */
export function forwardBackendDownloadResponse(response: Response): Response {
  const headers = new Headers()
  for (const name of SAFE_BACKEND_RESPONSE_HEADERS) {
    const value = response.headers.get(name)
    if (value) {
      headers.set(name, value)
    }
  }
  headers.set('Cache-Control', 'no-store')

  return new Response(BODYLESS_STATUSES.has(response.status) ? null : response.body, {
    status: response.status,
    headers,
  })
}

/** Streams an authenticated backend response; forwards Content-Type/Disposition and sets Cache-Control: private, no-store, Vary: Cookie. */
export function forwardAuthenticatedBackendResponse(response: Response): Response {
  const headers = new Headers()
  for (const name of SAFE_BACKEND_RESPONSE_HEADERS) {
    const value = response.headers.get(name)
    if (value) {
      headers.set(name, value)
    }
  }
  headers.set('Cache-Control', 'private, no-store')
  headers.set('Vary', 'Cookie')

  return new Response(BODYLESS_STATUSES.has(response.status) ? null : response.body, {
    status: response.status,
    headers,
  })
}
