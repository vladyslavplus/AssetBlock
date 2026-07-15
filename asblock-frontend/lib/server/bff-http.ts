import 'server-only'
import type { ZodError } from 'zod'

const SAFE_BACKEND_RESPONSE_HEADERS = ['content-type', 'content-disposition'] as const
const BODYLESS_STATUSES = new Set([204, 205, 304])

export function problemResponse(
  status: number,
  code: string,
  detail: string,
  errors?: Record<string, string[]>,
): Response {
  const body = {
    type: `urn:assetblock:error:${code}`,
    status,
    title: status === 403 ? 'Forbidden' : 'Request failed',
    detail,
    code,
    traceId: crypto.randomUUID(),
    ...(errors ? { errors } : {}),
  }

  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
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
  const origin = request.headers.get('Origin')
  if (!origin) {
    return problemResponse(403, 'ERR_ORIGIN_FORBIDDEN', 'A same-origin request is required.')
  }

  try {
    if (new URL(origin).origin !== new URL(request.url).origin) {
      return problemResponse(403, 'ERR_ORIGIN_FORBIDDEN', 'Cross-origin requests are not allowed.')
    }
  } catch {
    return problemResponse(403, 'ERR_ORIGIN_FORBIDDEN', 'The request origin is invalid.')
  }

  return null
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
