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
import { GET as adminAuditLogsGet } from '@/app/api/admin/audit-logs/route'
import { GET as sellerCollectionsGet } from '@/app/api/seller/collections/route'
import { GET as sellerListingsGet } from '@/app/api/seller/listings/route'
import { AUTH_COOKIE_ACCESS } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

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
    cookieStore.delete(AUTH_COOKIE_ACCESS)
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
    const validId = '123e4567-e89b-12d3-a456-426614174000'
    const res = await categoryPut(
      makeReq(`http://localhost:3000/api/admin/categories/${validId}`, 'PUT', { name: '' }),
      { params: Promise.resolve({ id: validId }) },
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
    const validId = '123e4567-e89b-12d3-a456-426614174000'
    const res = await tagPut(
      makeReq(`http://localhost:3000/api/admin/tags/${validId}`, 'PUT', { name: '' }),
      { params: Promise.resolve({ id: validId }) },
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
    const validId = '123e4567-e89b-12d3-a456-426614174000'
    const res = await sellerAssetPatch(
      makeReq(`http://localhost:3000/api/seller/assets/${validId}`, 'PATCH', { price: -5 }),
      { params: Promise.resolve({ id: validId }) },
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
    const validId = '123e4567-e89b-12d3-a456-426614174000'
    const res = await sellerAssetTagsPost(
      makeReq(`http://localhost:3000/api/seller/assets/${validId}/tags`, 'POST', { name: '   ' }),
      { params: Promise.resolve({ id: validId }) },
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
    const validAssetId = '123e4567-e89b-12d3-a456-426614174000'
    const res = await reviewsPost(
      makeReq(`http://localhost:3000/api/reviews/assets/${validAssetId}/reviews`, 'POST', {
        rating: 6,
      }),
      { params: Promise.resolve({ assetId: validAssetId }) },
    )
    expect(res.status).toBe(400)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const json = await res.json()
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.errors?.rating).toBeDefined()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('rejects invalid UUID route param on dynamic endpoints (E5)', async () => {
    vi.stubGlobal('fetch', fetchSpy)
    const res = await categoryPut(
      makeReq('http://localhost:3000/api/admin/categories/invalid-uuid', 'PUT', { name: 'Valid' }),
      { params: Promise.resolve({ id: 'invalid-uuid' }) },
    )
    expect(res.status).toBe(400)
    const json = await res.json()
    expect(json.code).toBe('ERR_VALIDATION_FAILED')
    expect(json.errors?.id).toBeDefined()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  describe('BFF List Routes Query Validation (E6)', () => {
    it('admin audit-logs GET accepts DENIED outcome and strips unknown params', async () => {
      cookieStore.set(AUTH_COOKIE_ACCESS, makeJwt(Math.floor(Date.now() / 1000) + 3600))
      fetchSpy.mockResolvedValue(new Response(JSON.stringify({ items: [] }), { status: 200 }))
      vi.stubGlobal('fetch', fetchSpy)

      const req = new Request(
        'http://localhost:3000/api/admin/audit-logs?outcome=DENIED&unknownParam=hacked',
      )
      const res = await adminAuditLogsGet(req)
      expect(res.status).toBe(200)

      expect(fetchSpy).toHaveBeenCalledTimes(1)
      const calledUrl = String(fetchSpy.mock.calls[0][0])
      expect(calledUrl).toContain('outcome=DENIED')
      expect(calledUrl).not.toContain('unknownParam')
      expect(calledUrl).not.toContain('page=')
      expect(calledUrl).not.toContain('pageSize=')
    })

    it('admin audit-logs GET rejects invalid outcome ERROR with 400 ProblemDetails', async () => {
      vi.stubGlobal('fetch', fetchSpy)
      const req = new Request('http://localhost:3000/api/admin/audit-logs?outcome=ERROR')
      const res = await adminAuditLogsGet(req)
      expect(res.status).toBe(400)
      const body = await res.json()
      expect(body.code).toBe('ERR_VALIDATION_FAILED')
      expect(body.errors?.outcome).toBeDefined()
      expect(fetchSpy).not.toHaveBeenCalled()
    })

    it('seller collections GET accepts PUBLISHED status and rejects unknown status', async () => {
      cookieStore.set(AUTH_COOKIE_ACCESS, makeJwt(Math.floor(Date.now() / 1000) + 3600))
      fetchSpy.mockResolvedValue(new Response(JSON.stringify({ items: [] }), { status: 200 }))
      vi.stubGlobal('fetch', fetchSpy)

      const validReq = new Request(
        'http://localhost:3000/api/seller/collections?status=PUBLISHED&page=2&pageSize=15',
      )
      const validRes = await sellerCollectionsGet(validReq)
      expect(validRes.status).toBe(200)
      const calledUrl = String(fetchSpy.mock.calls[0][0])
      expect(calledUrl).toContain('status=PUBLISHED')
      expect(calledUrl).toContain('page=2')
      expect(calledUrl).toContain('pageSize=15')

      fetchSpy.mockReset()
      const invalidReq = new Request(
        'http://localhost:3000/api/seller/collections?status=NON_EXISTENT_STATUS',
      )
      const invalidRes = await sellerCollectionsGet(invalidReq)
      expect(invalidRes.status).toBe(400)
      expect(fetchSpy).not.toHaveBeenCalled()
    })

    it('seller listings GET rejects minPrice > maxPrice with 400 ProblemDetails', async () => {
      vi.stubGlobal('fetch', fetchSpy)
      const req = new Request('http://localhost:3000/api/seller/listings?minPrice=50&maxPrice=10')
      const res = await sellerListingsGet(req)
      expect(res.status).toBe(400)
      const body = await res.json()
      expect(body.code).toBe('ERR_VALIDATION_FAILED')
      expect(body.errors?.minPrice).toBeDefined()
      expect(fetchSpy).not.toHaveBeenCalled()
    })
  })
})
