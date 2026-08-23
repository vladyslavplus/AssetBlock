import { afterEach, describe, expect, it, vi } from 'vitest'
import { z } from 'zod'

import { apiFetch, ApiRequestError } from '@/lib/http/api-client'
import { fetchBffJson } from '@/lib/http/bff-json'
import {
  applyApiFieldErrorsToForm,
  getApiErrorMessage,
  parseApiErrorBody,
} from '@/lib/http/api-errors'
import { isAbortError } from '@/lib/http/is-abort-error'

describe('isAbortError', () => {
  it('recognizes DOMException, named Error, and aborted signals', () => {
    expect(isAbortError(new DOMException('aborted', 'AbortError'))).toBe(true)
    const named = new Error('signal is aborted without reason')
    named.name = 'AbortError'
    expect(isAbortError(named)).toBe(true)
    expect(isAbortError(new Error('network down'))).toBe(false)
    const controller = new AbortController()
    controller.abort()
    expect(isAbortError(new TypeError('failed'), controller.signal)).toBe(true)
  })
})

describe('apiFetch and fetchBffJson', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('parses JSON success and empty bodies', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ id: '1' }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )
    await expect(apiFetch<{ id: string }>({ path: 'api/items' })).resolves.toEqual({ id: '1' })

    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('', { status: 200 })),
    )
    await expect(apiFetch({ path: 'api/items' })).resolves.toBeUndefined()
  })

  it('maps Problem Details without exposing stack traces', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              type: 'urn:assetblock:error:ERR_VALIDATION_FAILED',
              title: 'Validation failed',
              detail: 'Title is required.',
              code: 'ERR_VALIDATION_FAILED',
              errors: { Title: ['Title is required.'] },
            }),
            { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
          ),
      ),
    )
    await expect(apiFetch({ path: 'api/items', method: 'POST', jsonBody: {} })).rejects.toSatisfy(
      (error: unknown) => {
        expect(error).toBeInstanceOf(ApiRequestError)
        const err = error as ApiRequestError
        expect(err.message).toBe('Title is required.')
        expect(err.message).not.toMatch(/ZodError|stack/)
        expect(err.fieldErrors.title).toBe('Title is required.')
        return true
      },
    )
  })

  it('uses a safe fallback for non-JSON and unknown errors', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response('<html>boom</html>', {
            status: 500,
            headers: { 'Content-Type': 'text/html' },
          }),
      ),
    )
    await expect(apiFetch({ path: 'api/items' })).rejects.toBeInstanceOf(ApiRequestError)
    expect(getApiErrorMessage('<html>boom</html>', 'Something went wrong.')).toBe(
      'Something went wrong.',
    )
    expect(getApiErrorMessage({ nope: true }, 'Something went wrong.')).toBe(
      'Something went wrong.',
    )
  })

  it('throws schema mismatch as a generic error without Zod issue text', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ wrong: true }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )
    await expect(fetchBffJson('/api/test', z.object({ ok: z.boolean() }))).rejects.toSatisfy(
      (error: unknown) => {
        expect(error).toBeInstanceOf(Error)
        expect((error as Error).message).toMatch(/Invalid API response/)
        expect((error as Error).message).not.toMatch(/Expected boolean|ZodError/)
        return true
      },
    )
  })

  it('propagates abort from fetch and from body read', async () => {
    const abortError = new DOMException('The operation was aborted.', 'AbortError')
    const controller = new AbortController()
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        await new Promise<never>((_, reject) => {
          init?.signal?.addEventListener('abort', () => reject(abortError), { once: true })
        })
        return new Response('{}', { status: 200 })
      }),
    )
    const pending = fetchBffJson('/api/test', z.object({ ok: z.boolean() }), {
      signal: controller.signal,
    })
    queueMicrotask(() => controller.abort())
    await expect(pending).rejects.toBe(abortError)

    const bodyController = new AbortController()
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        return {
          ok: true,
          status: 200,
          text: async () => {
            throw abortError
          },
        } as unknown as Response
      }),
    )
    await expect(
      fetchBffJson('/api/test', z.object({ ok: z.boolean() }), { signal: bodyController.signal }),
    ).rejects.toBe(abortError)
  })

  it('does not treat a network error as cancellation', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('network down')
      }),
    )
    await expect(fetchBffJson('/api/test', z.object({ ok: z.boolean() }))).rejects.toSatisfy(
      (error: unknown) =>
        error instanceof TypeError && (error as TypeError).message === 'network down',
    )
  })

  it('forwards AbortSignal to fetch', async () => {
    const controller = new AbortController()
    let seen: AbortSignal | undefined
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        seen = init?.signal as AbortSignal
        return new Response(JSON.stringify({ ok: true }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }),
    )
    await fetchBffJson('/api/test', z.object({ ok: z.boolean() }), { signal: controller.signal })
    expect(seen).toBe(controller.signal)
  })
})

describe('parseApiErrorBody', () => {
  it('maps validation dictionaries onto form fields', () => {
    const parsed = parseApiErrorBody({
      errors: { 'Request.Title': ['Too short'] },
      detail: 'One or more validation errors occurred.',
    })
    expect(parsed?.fieldErrors.title).toBe('Too short')
    const fieldErrors = parsed?.fieldErrors
    expect(fieldErrors).toBeDefined()
    if (!fieldErrors) return
    const setError = vi.fn()
    applyApiFieldErrorsToForm(setError, fieldErrors)
    expect(setError).toHaveBeenCalledWith('title', { type: 'server', message: 'Too short' })
  })
})
