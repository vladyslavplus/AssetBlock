import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { z } from 'zod'

import { apiFetch, ApiRequestError } from './api-client.ts'
import { fetchBffJson } from './bff-json.ts'
import { isAbortError } from './is-abort-error.ts'

const originalFetch = globalThis.fetch

afterEach(() => {
  globalThis.fetch = originalFetch
  delete process.env.NEXT_PUBLIC_API_BASE_URL
})

test('isAbortError recognizes DOMException and Error AbortError', () => {
  assert.equal(isAbortError(new DOMException('aborted', 'AbortError')), true)
  const named = new Error('signal is aborted without reason')
  named.name = 'AbortError'
  assert.equal(isAbortError(named), true)
  assert.equal(isAbortError(new Error('network down')), false)
})

test('isAbortError is true when signal is already aborted', () => {
  const controller = new AbortController()
  controller.abort()
  assert.equal(isAbortError(new TypeError('failed'), controller.signal), true)
})

test('fetchBffJson passes AbortSignal to fetch', async () => {
  const controller = new AbortController()
  let seenSignal

  globalThis.fetch = async (_input, init) => {
    seenSignal = init?.signal
    return new Response(JSON.stringify({ ok: true }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  await fetchBffJson('/api/test', z.object({ ok: z.boolean() }), { signal: controller.signal })
  assert.equal(seenSignal, controller.signal)
})

test('fetchBffJson propagates AbortError from fetch without wrapping', async () => {
  const controller = new AbortController()
  const abortError = new DOMException('The operation was aborted.', 'AbortError')

  globalThis.fetch = async (_input, init) => {
    await new Promise((_, reject) => {
      init?.signal?.addEventListener('abort', () => reject(abortError), { once: true })
    })
    return new Response('{}', { status: 200 })
  }

  const pending = fetchBffJson('/api/test', z.object({ ok: z.boolean() }), {
    signal: controller.signal,
  })
  queueMicrotask(() => controller.abort())

  await assert.rejects(pending, (error) => {
    assert.equal(error, abortError)
    return true
  })
})

test('fetchBffJson does not treat schema errors as cancellation', async () => {
  globalThis.fetch = async () =>
    new Response(JSON.stringify({ wrong: true }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })

  await assert.rejects(fetchBffJson('/api/test', z.object({ ok: z.boolean() })), (error) => {
    assert.ok(error instanceof Error)
    assert.equal(error.name, 'Error')
    assert.match(error.message, /Invalid API response/)
    assert.equal(isAbortError(error), false)
    return true
  })
})

test('fetchBffJson propagates ordinary network errors', async () => {
  globalThis.fetch = async () => {
    throw new TypeError('network down')
  }

  await assert.rejects(fetchBffJson('/api/test', z.object({ ok: z.boolean() })), (error) => {
    assert.ok(error instanceof TypeError)
    assert.equal(error.message, 'network down')
    return true
  })
})

test('apiFetch passes AbortSignal to fetch', async () => {
  process.env.NEXT_PUBLIC_API_BASE_URL = 'https://api.example.test'
  const controller = new AbortController()
  let seenSignal

  globalThis.fetch = async (_input, init) => {
    seenSignal = init?.signal
    return new Response(JSON.stringify({ id: '1' }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  await apiFetch({ path: 'api/items', signal: controller.signal })
  assert.equal(seenSignal, controller.signal)
})

test('apiFetch propagates AbortError and does not map it to ApiRequestError', async () => {
  process.env.NEXT_PUBLIC_API_BASE_URL = 'https://api.example.test'
  const controller = new AbortController()
  const abortError = new DOMException('The operation was aborted.', 'AbortError')

  globalThis.fetch = async (_input, init) => {
    await new Promise((_, reject) => {
      init?.signal?.addEventListener('abort', () => reject(abortError), { once: true })
    })
    return new Response('{}', { status: 200 })
  }

  const pending = apiFetch({ path: 'api/items', signal: controller.signal })
  queueMicrotask(() => controller.abort())

  await assert.rejects(pending, (error) => {
    assert.equal(error, abortError)
    assert.equal(error instanceof ApiRequestError, false)
    return true
  })
})
