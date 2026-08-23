import type { z } from 'zod'

import { getApiErrorMessage } from './api-errors.ts'
import { isAbortError } from './is-abort-error.ts'

export type BffJsonResult<T> =
  | { ok: true; data: T }
  | { ok: false; status: number; message: string; body?: unknown }

function parseMaybeJson(text: string): unknown {
  if (!text) return undefined
  try {
    return JSON.parse(text) as unknown
  } catch {
    return text
  }
}

/**
 * BFF JSON GET/POST helper. Forwards AbortSignal to fetch so TanStack Query can cancel.
 * AbortError from fetch/body read propagates unchanged (do not wrap as a generic failure).
 */
export async function fetchBffJson<TSchema extends z.ZodTypeAny>(
  path: string,
  schema: TSchema,
  init?: RequestInit,
): Promise<BffJsonResult<z.infer<TSchema>>> {
  const response = await fetch(path, {
    credentials: 'include',
    ...init,
    headers: {
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...init?.headers,
    },
  })

  let text: string
  try {
    text = await response.text()
  } catch (error) {
    // Body read can reject with AbortError after headers when the query is cancelled.
    if (isAbortError(error, init?.signal)) throw error
    throw error
  }

  const body = parseMaybeJson(text)

  if (!response.ok) {
    return {
      ok: false,
      status: response.status,
      message: getApiErrorMessage(body, `Request failed (${response.status})`),
      body,
    }
  }

  const parsed = schema.safeParse(body)
  if (!parsed.success) {
    throw new Error(`Invalid API response from ${path}.`)
  }

  return { ok: true, data: parsed.data }
}
