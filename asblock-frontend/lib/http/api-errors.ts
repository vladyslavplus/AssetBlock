import type { FieldValues, Path, UseFormSetError } from 'react-hook-form'

/** Error entry returned by legacy AssetBlock `{ errors: [{ identifier, message }] }` bodies. */
export interface ApiErrorItem {
  identifier?: string
  message?: string
}

export interface ApiErrorsArrayBody {
  errors?: ApiErrorItem[]
}

const GENERIC_VALIDATION_DETAIL = 'One or more validation errors occurred.'
const TYPE_PREFIX = 'urn:assetblock:error:'

/** Friendly fallbacks when ProblemDetails detail is missing or generic. */
const FRIENDLY_ERROR_MESSAGES: Record<string, string> = {
  ERR_EMAIL_NOT_VERIFIED:
    'Email verification is required to perform this action. Verify your email on the Account page.',
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/** Reads an API response body without trusting its declared JSON shape. */
export async function readApiResponseBody(response: Response): Promise<unknown> {
  const text = await response.text()
  if (!text) {
    return undefined
  }

  const contentType = response.headers.get('Content-Type')?.toLowerCase() ?? ''
  if (!contentType.includes('json')) {
    return text
  }

  try {
    return JSON.parse(text) as unknown
  } catch {
    return text
  }
}

/**
 * Maps API property path (e.g. "Description", "Request.Title") to a typical RHF field name.
 */
export function apiPropertyPathToFormField(propertyPath: string): string {
  const segment = propertyPath.includes('.')
    ? propertyPath.slice(propertyPath.lastIndexOf('.') + 1)
    : propertyPath
  if (!segment) return propertyPath
  return segment.charAt(0).toLowerCase() + segment.slice(1)
}

export interface ParsedApiError {
  /** Human-readable text for toasts; multiple issues joined with "; ". */
  summary: string
  /** First message per field for react-hook-form `setError` (camelCase keys). */
  fieldErrors: Record<string, string>
  /** Stable AssetBlock error code when present (`ERR_*` or extensions.code). */
  code?: string
  /** Correlation id from ProblemDetails extensions. */
  traceId?: string
}

function collectFromValidationDictionary(
  errObj: Record<string, unknown>,
  fieldErrors: Record<string, string>,
  allMessages: string[],
): void {
  for (const [key, val] of Object.entries(errObj)) {
    const rawList = Array.isArray(val) ? val : val != null ? [val] : []
    const strMsgs = rawList
      .map((m) => (typeof m === 'string' ? m.trim() : String(m).trim()))
      .filter((m) => m.length > 0)
    if (strMsgs.length === 0) continue
    allMessages.push(...strMsgs)
    const formKey = apiPropertyPathToFormField(key)
    if (!(formKey in fieldErrors)) {
      const first = strMsgs[0]
      if (first) fieldErrors[formKey] = first
    }
  }
}

function collectFromErrorsArray(errors: unknown[], allMessages: string[]): void {
  for (const item of errors) {
    if (item && typeof item === 'object' && 'message' in item) {
      const m = (item as { message: unknown }).message
      if (typeof m === 'string' && m.trim()) {
        allMessages.push(m.trim())
        continue
      }
    }
    if (item && typeof item === 'object' && 'identifier' in item) {
      const id = (item as { identifier: unknown }).identifier
      if (typeof id === 'string' && id.trim()) {
        allMessages.push(id.trim())
        continue
      }
    }
    if (typeof item === 'string' && item.trim()) {
      allMessages.push(item.trim())
    }
  }
}

function readStringProp(o: Record<string, unknown>, key: string): string | undefined {
  const v = o[key]
  return typeof v === 'string' && v.trim() ? v.trim() : undefined
}

function readCodeAndTraceId(o: Record<string, unknown>): { code?: string; traceId?: string } {
  const extensions = isPlainObject(o.extensions) ? o.extensions : undefined
  const code =
    readStringProp(o, 'code') ??
    (extensions ? readStringProp(extensions, 'code') : undefined) ??
    extractCodeFromType(readStringProp(o, 'type'))
  const traceId =
    readStringProp(o, 'traceId') ?? (extensions ? readStringProp(extensions, 'traceId') : undefined)
  return { code, traceId }
}

function extractCodeFromType(type: string | undefined): string | undefined {
  if (!type?.startsWith(TYPE_PREFIX)) return undefined
  const code = type.slice(TYPE_PREFIX.length).trim()
  return code.length > 0 ? code : undefined
}

/**
 * Parses AssetBlock RFC 7807 ProblemDetails + validation dictionary + legacy `{ errors: [...] }` bodies.
 */
export function parseApiErrorBody(body: unknown): ParsedApiError | undefined {
  if (!isPlainObject(body)) {
    return undefined
  }

  const o = body
  const fieldErrors: Record<string, string> = {}
  const allMessages: string[] = []
  const { code, traceId } = readCodeAndTraceId(o)

  const errorsVal = o.errors

  if (errorsVal && typeof errorsVal === 'object' && !Array.isArray(errorsVal)) {
    collectFromValidationDictionary(errorsVal as Record<string, unknown>, fieldErrors, allMessages)
  }

  if (Array.isArray(errorsVal) && errorsVal.length > 0) {
    collectFromErrorsArray(errorsVal, allMessages)
  }

  if (typeof o.error === 'string' && o.error.trim()) {
    allMessages.push(o.error.trim())
  }

  const unique = [...new Set(allMessages)]
  let summary = unique.join('; ')

  if (!summary && typeof o.detail === 'string') {
    const d = o.detail.trim()
    if (d.length > 0 && d !== GENERIC_VALIDATION_DETAIL) {
      summary = d
    }
  }

  if (!summary && typeof o.title === 'string') {
    const t = o.title.trim()
    if (t.length > 0 && t !== 'Validation failed') {
      summary = t
    }
  }

  if (!summary && code) {
    summary = FRIENDLY_ERROR_MESSAGES[code] ?? code
  }

  if (code && summary === code && FRIENDLY_ERROR_MESSAGES[code]) {
    summary = FRIENDLY_ERROR_MESSAGES[code]
  }

  if (!summary) {
    return undefined
  }

  return {
    summary,
    fieldErrors,
    ...(code ? { code } : {}),
    ...(traceId ? { traceId } : {}),
  }
}

export function isApiErrorsBody(value: unknown): value is ApiErrorsArrayBody {
  return (
    typeof value === 'object' &&
    value !== null &&
    'errors' in value &&
    Array.isArray((value as ApiErrorsArrayBody).errors)
  )
}

/**
 * Parses JSON error body from a failed fetch; returns a short message for UI or logging.
 */
export function getMessageFromApiErrorBody(body: unknown): string | undefined {
  return parseApiErrorBody(body)?.summary
}

/** Same as {@link getMessageFromApiErrorBody} with a guaranteed non-empty fallback. */
export function getApiErrorMessage(body: unknown, fallback: string): string {
  return getMessageFromApiErrorBody(body) ?? fallback
}

/** Maps server validation keys (camelCase) onto react-hook-form fields. */
export function applyApiFieldErrorsToForm<T extends FieldValues>(
  setError: UseFormSetError<T>,
  fieldErrors: Record<string, string>,
): void {
  for (const [path, message] of Object.entries(fieldErrors)) {
    if (!message) continue
    setError(path as Path<T>, { type: 'server', message })
  }
}
