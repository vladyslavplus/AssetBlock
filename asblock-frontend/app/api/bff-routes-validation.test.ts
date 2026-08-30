import { afterEach, describe, expect, it, vi } from 'vitest'

import { POST as categoriesPost } from '@/app/api/admin/categories/route'
import { PUT as categoryPut } from '@/app/api/admin/categories/[id]/route'
import { POST as tagsPost } from '@/app/api/admin/tags/route'
import { PUT as tagPut } from '@/app/api/admin/tags/[id]/route'
import { PATCH as accountMePatch } from '@/app/api/account/me/route'
import { PUT as accountSocialsPut } from '@/app/api/account/socials/route'
import { PATCH as sellerAssetPatch } from '@/app/api/seller/assets/[id]/route'
import { POST as sellerAssetTagsPost } from '@/app/api/seller/assets/[id]/tags/route'
import { POST as reviewsPost } from '@/app/api/reviews/assets/[assetId]/reviews/route'
import { createMemoryCookieStore } from '@/test/cookie-store'

const cookieStore = createMemoryCookieStore()

vi.mock('next/headers', () => ({
  cookies: async () => cookieStore,
}))

vi.mock('server-only', () => ({}))

function makeReq(
  url: string,
  method: string,
  body: unknown,
  origin = 'http://localhost:3000',
): Request {
  return new Request(url, {
    method,
    headers: {
      Origin: origin,
      'Content-Type': 'application/json',
    },
    body: typeof body === 'string' ? body : JSON.stringify(body),
  })
}

describe('BFF Mutating Routes Zod Validation (E3)', () => {
  const fetchSpy = vi.fn()

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchSpy.mockReset()
  })

  it('admin categories POST rejects invalid payload with canonical 400 ProblemDetails', async () => {
    vi.stubGlobal('fetch', fetchSpy)
    const res = await categoriesPost(
      makeReq('http://localhost:3000/api/admin/categories', 'POST', { name: '' }),
    )
    expect(res.status).toBe(400)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const json = await res.json()
    expect(json.type).toBe('urn:assetblock:error:ERR_VALIDATION_FAILED')
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.status).toBe(400)
    expect(json.title).toBe('Request failed')
    expect(json.detail).toBe('One or more validation errors occurred.')
    expect(json.traceId).toBeDefined()
    expect(json.errors?.name).toBeDefined()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('admin categories POST rejects malformed JSON with canonical 400 ProblemDetails', async () => {
    vi.stubGlobal('fetch', fetchSpy)
    const res = await categoriesPost(
      makeReq('http://localhost:3000/api/admin/categories', 'POST', '{ malformed json'),
    )
    expect(res.status).toBe(400)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const json = await res.json()
    expect(json.type).toBe('urn:assetblock:error:ERR_VALIDATION_FAILED')
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.detail).toBe('The request body must be valid JSON.')
    expect(json.errors?.body).toContain('Invalid JSON body.')
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('admin categories PUT rejects invalid payload with 400 ProblemDetails', async () => {
    vi.stubGlobal('fetch', fetchSpy)
    const res = await categoryPut(
      makeReq('http://localhost:3000/api/admin/categories/123', 'PUT', { name: '' }),
      { params: Promise.resolve({ id: '123' }) },
    )
    expect(res.status).toBe(400)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const json = await res.json()
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.errors?.name).toBeDefined()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('admin tags POST rejects invalid payload with 400 ProblemDetails', async () => {
    vi.stubGlobal('fetch', fetchSpy)
    const res = await tagsPost(
      makeReq('http://localhost:3000/api/admin/tags', 'POST', { name: '' }),
    )
    expect(res.status).toBe(400)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const json = await res.json()
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.errors?.name).toBeDefined()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('admin tags PUT rejects invalid payload with 400 ProblemDetails', async () => {
    vi.stubGlobal('fetch', fetchSpy)
    const res = await tagPut(
      makeReq('http://localhost:3000/api/admin/tags/123', 'PUT', { name: '' }),
      { params: Promise.resolve({ id: '123' }) },
    )
    expect(res.status).toBe(400)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const json = await res.json()
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.errors?.name).toBeDefined()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('account me PATCH rejects invalid avatar URL with 400 ProblemDetails', async () => {
    vi.stubGlobal('fetch', fetchSpy)
    const res = await accountMePatch(
      makeReq('http://localhost:3000/api/account/me', 'PATCH', { avatarUrl: 'invalid-url' }),
    )
    expect(res.status).toBe(400)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const json = await res.json()
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.errors?.avatarUrl).toBeDefined()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('account socials PUT rejects invalid platformId/url with 400 ProblemDetails', async () => {
    vi.stubGlobal('fetch', fetchSpy)
    const res = await accountSocialsPut(
      makeReq('http://localhost:3000/api/account/socials', 'PUT', {
        links: [{ platformId: 'not-a-uuid', url: 'not-a-url' }],
      }),
    )
    expect(res.status).toBe(400)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const json = await res.json()
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.errors?.['links.0.platformId']).toBeDefined()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('seller asset PATCH rejects invalid price with 400 ProblemDetails', async () => {
    vi.stubGlobal('fetch', fetchSpy)
    const res = await sellerAssetPatch(
      makeReq('http://localhost:3000/api/seller/assets/123', 'PATCH', { price: -5 }),
      { params: Promise.resolve({ id: '123' }) },
    )
    expect(res.status).toBe(400)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const json = await res.json()
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.errors?.price).toBeDefined()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('seller asset tags POST rejects empty tag name with 400 ProblemDetails', async () => {
    vi.stubGlobal('fetch', fetchSpy)
    const res = await sellerAssetTagsPost(
      makeReq('http://localhost:3000/api/seller/assets/123/tags', 'POST', { name: '   ' }),
      { params: Promise.resolve({ id: '123' }) },
    )
    expect(res.status).toBe(400)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const json = await res.json()
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.errors?.name).toBeDefined()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('reviews POST rejects invalid rating with 400 ProblemDetails', async () => {
    vi.stubGlobal('fetch', fetchSpy)
    const res = await reviewsPost(
      makeReq('http://localhost:3000/api/reviews/assets/123/reviews', 'POST', { rating: 6 }),
      { params: Promise.resolve({ assetId: '123' }) },
    )
    expect(res.status).toBe(400)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const json = await res.json()
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.errors?.rating).toBeDefined()
    expect(fetchSpy).not.toHaveBeenCalled()
  })
})
