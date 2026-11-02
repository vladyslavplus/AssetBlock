import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { GET as queueGet } from './submissions/route'
import { GET as caseGet } from './submissions/[id]/route'
import { POST as claimPost } from './submissions/[id]/claim/route'
import { POST as releasePost } from './submissions/[id]/release/route'
import { POST as decisionPost } from './submissions/[id]/decision/route'
import { GET as caseEvidenceGet } from './submissions/[id]/evidence/[kind]/route'
import { GET as coverageQueueGet } from './coverage-reviews/route'
import { POST as coverageAssessmentPost } from './coverage-reviews/[id]/assessments/route'
import { GET as coverageEvidenceGet } from './coverage-reviews/[id]/evidence/[kind]/route'
import { AUTH_COOKIE_ACCESS } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

let store = createMemoryCookieStore()
vi.mock('next/headers', () => ({ cookies: async () => store }))
vi.mock('server-only', () => ({}))

const id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const hash = 'a'.repeat(64)
const context = { params: Promise.resolve({ id }) }
function request(path: string, method = 'GET', value?: unknown) {
  return new Request(`http://localhost:3000${path}`, {
    method,
    headers: { Origin: 'http://localhost:3000', 'Content-Type': 'application/json' },
    ...(value === undefined ? {} : { body: JSON.stringify(value) }),
  })
}

describe('moderation BFF routes', () => {
  beforeEach(() => {
    store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: makeJwt(Math.floor(Date.now() / 1000) + 3600),
    })
    vi.stubEnv('ASSETBLOCK_API_BASE_URL', 'https://api.example.test')
    vi.stubEnv('NEXT_PUBLIC_API_BASE_URL', 'https://api.example.test')
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
  })

  it('forwards normalized keyset queue query through shared endpoint', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('{"items":[],"nextCursor":null}')),
    )
    const response = await queueGet(
      request('/api/moderation/submissions?state=IN_REVIEW&pageSize=20&claimFilter=MINE'),
    )
    expect(response.status).toBe(200)
    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(
      'https://api.example.test/api/moderation/submissions?claimFilter=MINE&pageSize=20&state=IN_REVIEW',
    )
  })

  it('rejects invalid queue filters before backend call', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    expect((await queueGet(request('/api/moderation/submissions?state=APPROVED'))).status).toBe(400)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('rejects foreign-origin claim writes before parsing or forwarding', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const foreign = new Request(`http://localhost:3000/api/moderation/submissions/${id}/claim`, {
      method: 'POST',
      headers: { Origin: 'https://foreign.example' },
      body: '{',
    })
    expect((await claimPost(foreign, context)).status).toBe(403)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it.each([
    ['claim', claimPost],
    ['release', releasePost],
  ] as const)(
    'validates same-origin %s writes and forwards expected revision',
    async (action, handler) => {
      const fetchMock = vi.fn(
        async (_input: RequestInfo | URL, _init?: RequestInit) => new Response('{}'),
      )
      vi.stubGlobal('fetch', fetchMock)
      const response = await handler(
        request(`/api/moderation/submissions/${id}/${action}`, 'POST', {
          operationId: id,
          expectedCaseRevision: 3,
        }),
        context,
      )
      expect(response.status).toBe(200)
      expect(fetchMock.mock.calls[0]?.[0]).toBe(
        `https://api.example.test/api/moderation/submissions/${id}/${action}`,
      )
      const init = fetchMock.mock.calls[0]?.[1] as RequestInit
      expect(JSON.parse(init.body as string)).toEqual({ operationId: id, expectedCaseRevision: 3 })
      const invalid = await handler(
        request(`/api/moderation/submissions/${id}/${action}`, 'POST', {
          operationId: id,
          expectedCaseRevision: 0,
        }),
        context,
      )
      expect(invalid.status).toBe(400)
    },
  )

  it('proxies case reads by validated ID and decision writes with strict body validation', async () => {
    const fetchMock = vi.fn(
      async (_input: RequestInfo | URL, _init?: RequestInit) => new Response('{}'),
    )
    vi.stubGlobal('fetch', fetchMock)
    expect((await caseGet(request(`/api/moderation/submissions/${id}`), context)).status).toBe(200)
    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      `https://api.example.test/api/moderation/submissions/${id}`,
    )
    const body = {
      operationId: id,
      basis: {
        versionId: id,
        contentSha256: hash,
        analysisReportId: id,
        declarationRevision: 1,
        materialMetadataRevision: 1,
        policyVersion: 'policy-v1',
        sellerEvidenceRevision: 1,
        caseRevision: 3,
      },
      outcome: 'APPROVE',
      rationale: 'Reviewed',
      dispositions: [],
    }
    expect(
      (
        await decisionPost(
          request(`/api/moderation/submissions/${id}/decision`, 'POST', body),
          context,
        )
      ).status,
    ).toBe(200)
    const invalid = await decisionPost(
      request(`/api/moderation/submissions/${id}/decision`, 'POST', { ...body, ignored: true }),
      context,
    )
    expect(invalid.status).toBe(400)
  })

  it('validates and forwards case evidence previews without cacheable response headers', async () => {
    const fetchMock = vi.fn(
      async (_input: RequestInfo | URL, _init?: RequestInit) =>
        new Response('{}', { headers: { 'Cache-Control': 'private, no-store' } }),
    )
    vi.stubGlobal('fetch', fetchMock)
    const response = await caseEvidenceGet(
      request(`/api/moderation/submissions/${id}/evidence/DECLARATION`),
      {
        params: Promise.resolve({ id, kind: 'DECLARATION' }),
      },
    )
    expect(response.status).toBe(200)
    expect(response.headers.get('Cache-Control')).toBe('private, no-store')
    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      `https://api.example.test/api/moderation/submissions/${id}/evidence/DECLARATION`,
    )
    expect(
      (
        await caseEvidenceGet(
          request(`/api/moderation/submissions/${id}/evidence/DECLARATION?extra=x`),
          {
            params: Promise.resolve({ id, kind: 'DECLARATION' }),
          },
        )
      ).status,
    ).toBe(400)
  })

  it('validates coverage queue, assessment and coverage evidence routes', async () => {
    const fetchMock = vi.fn(
      async (_input: RequestInfo | URL, _init?: RequestInit) => new Response('{}'),
    )
    vi.stubGlobal('fetch', fetchMock)
    expect(
      (await coverageQueueGet(request('/api/moderation/coverage-reviews?page=2'))).status,
    ).toBe(200)
    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      'https://api.example.test/api/moderation/coverage-reviews?page=2',
    )
    expect(
      (await coverageQueueGet(request('/api/moderation/coverage-reviews?page=0'))).status,
    ).toBe(400)

    const assessment = {
      operationId: id,
      expectedRequestRevision: 2,
      expectedTupleDigest: hash,
      outcome: 'BLOCKED',
      competence: ['Asset review'],
      scope: [],
      manageableScope: false,
      reviewPlan: 'Manual review',
      completedPlanReference: '',
      rationale: 'Scope cannot be read.',
    }
    const saved = await coverageAssessmentPost(
      request(`/api/moderation/coverage-reviews/${id}/assessments`, 'POST', assessment),
      { params: Promise.resolve({ id }) },
    )
    expect(saved.status).toBe(200)
    expect(fetchMock.mock.calls[1]?.[0]).toBe(
      `https://api.example.test/api/moderation/coverage-reviews/${id}/assessments`,
    )
    expect(
      (
        await coverageAssessmentPost(
          request(`/api/moderation/coverage-reviews/${id}/assessments`, 'POST', {
            ...assessment,
            unknown: true,
          }),
          { params: Promise.resolve({ id }) },
        )
      ).status,
    ).toBe(400)

    const evidence = await coverageEvidenceGet(
      request(`/api/moderation/coverage-reviews/${id}/evidence/COVERAGE_INVENTORY?findingId=${id}`),
      {
        params: Promise.resolve({ id, kind: 'COVERAGE_INVENTORY' }),
      },
    )
    expect(evidence.status).toBe(200)
    expect(fetchMock.mock.calls[2]?.[0]).toBe(
      `https://api.example.test/api/moderation/coverage-reviews/${id}/evidence/COVERAGE_INVENTORY?findingId=${id}`,
    )
  })
})
