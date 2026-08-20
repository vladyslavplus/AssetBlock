import type { z } from 'zod'

import { getApiErrorMessage } from '@/lib/http/api-errors'

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
  const body = parseMaybeJson(await response.text())

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
