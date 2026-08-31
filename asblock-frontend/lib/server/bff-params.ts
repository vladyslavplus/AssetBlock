import 'server-only'
import { z } from 'zod'
import { problemResponse } from '@/lib/server/bff-http'

const uuidSchema = z.string().uuid()

export type ParsedUuidResult = { ok: true; value: string } | { ok: false; response: Response }

export type ParsedOptionalUuidResult =
  | { ok: true; value: string | null }
  | { ok: false; response: Response }

/**
 * Validates that a route param or query param is a non-empty, valid UUID format.
 * Returns { ok: true, value } on success, or { ok: false, response } with a 400 ProblemDetails.
 */
export function parseUuidParam(name: string, value: string | null | undefined): ParsedUuidResult {
  if (value == null || value.trim() === '') {
    return {
      ok: false,
      response: problemResponse(
        400,
        'ERR_VALIDATION_FAILED',
        `The parameter '${name}' is required.`,
        {
          [name]: [`The parameter '${name}' is required.`],
        },
      ),
    }
  }

  const result = uuidSchema.safeParse(value.trim())
  if (!result.success) {
    return {
      ok: false,
      response: problemResponse(
        400,
        'ERR_VALIDATION_FAILED',
        `The parameter '${name}' must be a valid UUID.`,
        {
          [name]: [`The parameter '${name}' must be a valid UUID.`],
        },
      ),
    }
  }

  return { ok: true, value: result.data }
}

/**
 * Validates that an optional route param or query param is either null/empty or a valid UUID format.
 */
export function parseOptionalUuidParam(
  name: string,
  value: string | null | undefined,
): ParsedOptionalUuidResult {
  if (value == null || value.trim() === '') {
    return { ok: true, value: null }
  }

  const result = uuidSchema.safeParse(value.trim())
  if (!result.success) {
    return {
      ok: false,
      response: problemResponse(
        400,
        'ERR_VALIDATION_FAILED',
        `The parameter '${name}' must be a valid UUID.`,
        {
          [name]: [`The parameter '${name}' must be a valid UUID.`],
        },
      ),
    }
  }

  return { ok: true, value: result.data }
}
