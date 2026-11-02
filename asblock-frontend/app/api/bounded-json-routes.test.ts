import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { POST as decision } from '@/app/api/moderation/submissions/[id]/decision/route'
import { POST as claim } from '@/app/api/moderation/submissions/[id]/claim/route'
import { POST as release } from '@/app/api/moderation/submissions/[id]/release/route'
import { POST as assessment } from '@/app/api/moderation/coverage-reviews/[id]/assessments/route'
import { POST as responses } from '@/app/api/seller/assets/[id]/versions/[versionId]/responses/route'
import { POST as appeal } from '@/app/api/seller/assets/[id]/versions/[versionId]/appeals/route'
import { PUT as evidence } from '@/app/api/seller/assets/[id]/versions/[versionId]/evidence/route'
import { POST as sellerCoverage } from '@/app/api/seller/assets/[id]/versions/[versionId]/coverage-reviews/route'
import { POST as retry } from '@/app/api/seller/assets/[id]/versions/[versionId]/analysis-retries/route'
import { POST as submit } from '@/app/api/seller/assets/[id]/versions/[versionId]/submissions/route'
import { POST as withdraw } from '@/app/api/seller/submissions/[submissionId]/withdraw/route'
import { BACKEND_JSON_BODY_LIMIT_BYTES } from '@/lib/server/bff-http'
import { AUTH_COOKIE_ACCESS } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

let cookieStore = createMemoryCookieStore()
vi.mock('next/headers', () => ({ cookies: async () => cookieStore }))

const id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const versionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const contextForSubmission = { params: Promise.resolve({ id }) }
const contextForVersion = { params: Promise.resolve({ id, versionId }) }
const contextForWithdraw = { params: Promise.resolve({ submissionId: id }) }

function payloadFor(limit: keyof typeof BACKEND_JSON_BODY_LIMIT_BYTES) {
  switch (limit) {
    case 'moderationDecision':
      return {
        operationId: id,
        basis: {
          versionId,
          contentSha256: 'a'.repeat(64),
          analysisReportId: id,
          declarationRevision: 1,
          materialMetadataRevision: 1,
          policyVersion: 'p'.repeat(64),
          sellerEvidenceRevision: 1,
          caseRevision: 1,
        },
        outcome: 'REJECT',
        rationale: 'r'.repeat(2_000) + ' '.repeat(2_000),
        dispositions: Array.from({ length: 200 }, () => ({
          findingId: versionId,
          disposition: 'EXPLAINED',
          rationale: 'd'.repeat(2_000) + ' '.repeat(2_000),
        })),
      }
    case 'moderationClaim':
    case 'moderationRelease':
      return { operationId: id, expectedCaseRevision: 1 }
    case 'coverageAssessment':
      return {
        operationId: id,
        expectedRequestRevision: 1,
        expectedTupleDigest: 'a'.repeat(64),
        outcome: 'BLOCKED',
        competence: Array.from({ length: 20 }, () => 'c'.repeat(60)),
        scope: Array.from({ length: 20 }, () => ({
          path: '\u{1F600}'.repeat(512),
          language: '\u{1F600}'.repeat(60),
          roleInCoreLogic: '\u{1F600}'.repeat(200),
          readable: false,
          evidenceKind: 'ARCHIVE_README',
          findingId: versionId,
        })),
        manageableScope: false,
        reviewPlan: '\u{1F600}'.repeat(2_000),
        completedPlanReference: '\u{1F600}'.repeat(200),
        rationale: '\u{1F600}'.repeat(2_000),
      }
    case 'sellerResponse':
      return {
        workspaceId: id,
        reportId: versionId,
        operationId: id,
        expectedWorkspaceRevision: 1,
        expectedEvidenceRevision: 0,
        text: '\u{1F600}'.repeat(2_000),
        findingId: versionId,
        responseType: 'PRIOR_WORK',
      }
    case 'sellerAppeal':
      return {
        workspaceId: id,
        expectedWorkspaceRevision: 1,
        expectedCaseRevision: 1,
        operationId: id,
        rationale: '\u{1F600}'.repeat(2_000) + ' '.repeat(2_000),
      }
    case 'sellerEvidence':
      return {
        workspaceId: id,
        reportId: versionId,
        operationId: id,
        expectedWorkspaceRevision: 1,
        expectedEvidenceRevision: 0,
        text: '\u{1F600}'.repeat(2_000),
      }
    case 'sellerCoverageRequest':
      return {
        operationId: id,
        workspaceId: id,
        reportId: versionId,
        expectedWorkspaceRevision: 1,
      }
    case 'sellerSubmission':
      return {
        workspaceId: id,
        expectedWorkspaceRevision: 1,
        expectedCaseRevision: 0,
        operationId: versionId,
      }
    case 'sellerWithdraw':
      return { expectedCaseRevision: 1, operationId: id }
    case 'sellerAnalysisRetry':
      return {
        operationId: id,
        expectedRunId: versionId,
        expectedWorkspaceRevision: 1,
        expectedPolicyDigest: '\u{1F600}'.repeat(64),
        expectedInputDigest: '\u{1F600}'.repeat(64),
      }
  }
}

const routes = [
  ['moderation decision', decision, contextForSubmission, 'moderationDecision', 'POST'],
  ['moderation claim', claim, contextForSubmission, 'moderationClaim', 'POST'],
  ['moderation release', release, contextForSubmission, 'moderationRelease', 'POST'],
  [
    'moderation coverage assessment',
    assessment,
    contextForSubmission,
    'coverageAssessment',
    'POST',
  ],
  ['seller finding responses', responses, contextForVersion, 'sellerResponse', 'POST'],
  ['seller appeals', appeal, contextForVersion, 'sellerAppeal', 'POST'],
  ['seller evidence', evidence, contextForVersion, 'sellerEvidence', 'PUT'],
  ['seller coverage reviews', sellerCoverage, contextForVersion, 'sellerCoverageRequest', 'POST'],
  ['seller analysis retries', retry, contextForVersion, 'sellerAnalysisRetry', 'POST'],
  ['seller submissions', submit, contextForVersion, 'sellerSubmission', 'POST'],
  ['seller withdrawal', withdraw, contextForWithdraw, 'sellerWithdraw', 'POST'],
] as const

interface RouteContext {
  params: Promise<Record<string, string>>
}
type RouteHandler = (request: Request, context: RouteContext) => Promise<Response>

function bodyAtLimit(payload: unknown, limit: number): string {
  const json = JSON.stringify(payload)
  const byteLength = new TextEncoder().encode(json).byteLength
  if (byteLength > limit) throw new Error(`Payload (${byteLength}) exceeds route limit (${limit}).`)
  return json + ' '.repeat(limit - byteLength)
}

function writeRequest(method: string, body: string) {
  return new Request('http://localhost:3000/api/review', {
    method,
    headers: {
      Origin: 'http://localhost:3000',
      'Content-Type': 'application/json',
      'Content-Length': String(new TextEncoder().encode(body).byteLength),
    },
    body,
  })
}

beforeEach(() => {
  cookieStore = createMemoryCookieStore({
    [AUTH_COOKIE_ACCESS]: makeJwt(Math.floor(Date.now() / 1000) + 3600),
  })
  vi.stubEnv('ASSETBLOCK_API_BASE_URL', 'https://api.example.test')
  vi.stubEnv('NEXT_PUBLIC_API_BASE_URL', 'https://api.example.test')
})
afterEach(() => {
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
})

describe('bounded JSON request bodies on review routes', () => {
  it.each(routes)(
    'accepts max legitimate payload at route ceiling: %s',
    async (_name, route, context, key, method) => {
      const limit = BACKEND_JSON_BODY_LIMIT_BYTES[key]
      const backendFetch = vi.fn(async () => new Response('{}', { status: 200 }))
      vi.stubGlobal('fetch', backendFetch)
      const body = bodyAtLimit(payloadFor(key), limit)

      const response = await (route as unknown as RouteHandler)(
        writeRequest(method, body),
        context as RouteContext,
      )

      expect(response.status, await response.clone().text()).toBe(200)
      expect(backendFetch).toHaveBeenCalledTimes(1)
    },
  )

  it.each(routes)(
    'rejects one byte over route ceiling before forwarding: %s',
    async (_name, route, context, key, method) => {
      const limit = BACKEND_JSON_BODY_LIMIT_BYTES[key]
      const backendFetch = vi.fn()
      vi.stubGlobal('fetch', backendFetch)
      const body = `${bodyAtLimit(payloadFor(key), limit)} `

      const response = await (route as unknown as RouteHandler)(
        writeRequest(method, body),
        context as RouteContext,
      )

      expect(response.status).toBe(413)
      expect(backendFetch).not.toHaveBeenCalled()
    },
  )
})
