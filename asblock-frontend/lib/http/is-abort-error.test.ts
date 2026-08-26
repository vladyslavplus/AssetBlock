import { afterEach, describe, expect, it, vi } from 'vitest'

import { fetchCatalogFacets } from '@/lib/catalog/catalog-query'
import { isAbortError, keepAbortable, toAbortError } from '@/lib/http/is-abort-error'

describe('isAbortError', () => {
  it('does not classify a real network failure as abort', () => {
    expect(isAbortError(new TypeError('Failed to fetch'))).toBe(false)
    expect(isAbortError(new Error('Failed to fetch'))).toBe(false)
  })

  it('classifies an aborted signal as cancellation even when fetch surfaces TypeError', () => {
    const controller = new AbortController()
    controller.abort()
    expect(isAbortError(new TypeError('Failed to fetch'), controller.signal)).toBe(true)
    expect(toAbortError(new TypeError('Failed to fetch'), controller.signal).name).toBe(
      'AbortError',
    )
  })
})

describe('fetchCatalogFacets cancellation', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('does not leave an unhandled rejection when sibling category/tag fetches abort', async () => {
    const controller = new AbortController()
    const abort = new DOMException('The operation was aborted.', 'AbortError')
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        if (init?.signal?.aborted) {
          throw abort
        }
        await new Promise<never>((_, reject) => {
          init?.signal?.addEventListener('abort', () => reject(abort))
        })
        throw abort
      }),
    )

    const unhandled: unknown[] = []
    const onUnhandled = (reason: unknown) => {
      unhandled.push(reason)
    }
    process.on('unhandledRejection', onUnhandled)

    const pending = fetchCatalogFacets({ signal: controller.signal })
    controller.abort()
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' })
    await new Promise((resolve) => setTimeout(resolve, 20))
    process.off('unhandledRejection', onUnhandled)
    expect(unhandled).toEqual([])
  })
})

describe('keepAbortable', () => {
  it('rethrows AbortError without swallowing network failures', async () => {
    await expect(
      keepAbortable(Promise.reject(new DOMException('aborted', 'AbortError'))),
    ).rejects.toMatchObject({ name: 'AbortError' })
    await expect(keepAbortable(Promise.reject(new Error('boom')))).rejects.toThrow('boom')
  })
})
