/**
 * Deterministic local API for Playwright SSR + client catalog fetches.
 * No Stripe, no real credentials, no production traffic.
 */
import http from 'node:http'
import { z } from 'zod'
import { assetVersionsResponseSchema } from '../lib/library/library-schemas.ts'
import {
  claimChangeRequestSchema,
  moderationCaseSummarySchema,
  moderationDecisionResponseSchema,
  moderationDecisionRequestSchema,
  moderatorQueuePageSchema,
  claimChangeResponseSchema,
  restrictedEvidenceResponseSchema,
  coverageQueuePageSchema,
  coverageAssessmentRequestSchema,
  coverageAssessmentResponseSchema,
} from '../lib/moderation/moderation-schemas.ts'
import { sellerAssetDetailSchema } from '../lib/seller/seller-asset-schemas.ts'
import { assetProcessingJobSchema } from '../lib/seller/seller-processing-schemas.ts'
import {
  parseAssetUploadMetadata,
  parsePublishVersionMetadata,
  validateArchiveUploadFilename,
} from '../lib/seller/seller-multipart-schemas.ts'
import {
  moderationCaseChangedMessageSchema,
  pagedNotificationsSchema,
} from '../lib/notifications/notification-types.ts'
import { tokensResponseSchema } from '../lib/auth/tokens-schema.ts'
import {
  sellerAnalysisFindingsPageSchema,
  sellerAnalysisSummarySchema,
  appealRequestSchema,
  assetVersionSubmissionRequestSchema,
  sellerDraftCreateRequestSchema,
  sellerDraftSaveRequestSchema,
  sellerDeclarationSaveRequestSchema,
  sellerDeclarationSnapshotSchema,
  sellerDraftCreatedSchema,
  draftSaveResultSchema,
  sellerDraftSnapshotSchema,
  sellerEvidenceSavedSchema,
  sellerResponseRequestSchema,
  sellerResponsesPageSchema,
  submissionWithdrawRequestSchema,
  sellerSubmissionCreatedSchema,
  sellerSubmissionWithdrawnSchema,
  sellerVersionReviewSchema,
} from '../lib/seller/seller-draft-schemas.ts'

const PORT = Number(process.env.PLAYWRIGHT_API_PORT ?? 3999)

const assetId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const categoryId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const collectionId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'
const authorId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const versionId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'
const secondVersionId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeef'

const license = {
  code: 'PERSONAL',
  displayName: 'Personal use',
  templateVersion: '1',
  terms: 'Personal use only.',
}

const assetListItem = {
  id: assetId,
  title: 'Procedural Shader Kit',
  description: 'A test asset',
  price: 19,
  categoryId,
  categoryName: 'Shaders',
  authorId,
  authorUsername: 'maya',
  createdAt: '2026-01-01T00:00:00.000Z',
  tags: ['unity'],
  averageRating: 5,
}

const shaderSearchItem = {
  ...assetListItem,
  id: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
  title: 'Shader Noise Pack',
  description: 'Filtered search result',
  price: 9,
  averageRating: 4,
}

const assetDetail = {
  ...assetListItem,
  updatedAt: '2026-01-01T00:00:00.000Z',
  currentVersionNumber: 1,
  currentVersionId: versionId,
  currentVersionCreatedAt: '2026-01-01T00:00:00.000Z',
  currentFileName: 'pack.zip',
  currentContentLength: 128,
  currentContentSha256: 'a'.repeat(64),
  currentLicense: license,
}

const collectionDetail = {
  id: collectionId,
  title: 'Starter Collection',
  description: null,
  status: 'PUBLISHED',
  publishedAt: '2026-01-01T00:00:00.000Z',
  archivedAt: null,
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: '2026-01-01T00:00:00.000Z',
  sellerId: authorId,
  sellerUsername: 'maya',
  items: [
    {
      assetId,
      title: assetListItem.title,
      price: assetListItem.price,
      position: 1,
      isAvailable: true,
      unavailableReason: null,
    },
  ],
}

function corsHeaders(res) {
  const origin = res.req?.headers.origin
  const requestHeaders = res.req?.headers['access-control-request-headers']
  return {
    'Access-Control-Allow-Origin': origin ?? '*',
    ...(origin ? { 'Access-Control-Allow-Credentials': 'true', Vary: 'Origin' } : {}),
    'Access-Control-Allow-Headers':
      requestHeaders ?? 'content-type,authorization,x-requested-with,x-signalr-user-agent',
    'Access-Control-Allow-Methods': 'GET,POST,PUT,PATCH,DELETE,OPTIONS',
  }
}

function sendText(res, status, body) {
  res.writeHead(status, { ...corsHeaders(res), 'Content-Type': 'text/plain; charset=UTF-8' })
  res.end(body)
}

function send(res, status, body) {
  const json = JSON.stringify(body)
  res.writeHead(status, { ...corsHeaders(res), 'Content-Type': 'application/json' })
  res.end(json)
}

const ACCESS_OK = 'playwright-access-ok'
const sessionUser = {
  id: '11111111-1111-4111-8111-111111111111',
  username: 'seller',
  role: 'User',
  emailVerifiedAt: '2026-01-01T00:00:00.000Z',
  avatarUrl: null,
  bio: null,
  isPublicProfile: true,
  createdAt: '2026-01-01T00:00:00.000Z',
  socialLinks: [],
}
const refreshByToken = Object.create(null)
const mutationReceipts = new Map()
const signalRConnections = new Map()
const signalRStats = {
  tokenRequests: 0,
  negotiations: 0,
  polls: 0,
  transportPosts: 0,
  handshakeBodyBytes: 0,
  handshakes: 0,
}

function sendSignalREvent(connection, event) {
  if (connection.pendingPoll) {
    const response = connection.pendingPoll
    connection.pendingPoll = null
    sendText(response, 200, event)
  } else {
    connection.messages.push(event)
  }
}

const e2eResetRequestSchema = z
  .object({
    scenario: z.string().optional(),
    role: z.enum(['User', 'Moderator']).optional(),
    state: z.string().optional(),
    kind: z.enum(['STANDARD', 'APPEAL']).optional(),
    activeSubmission: z.boolean().optional(),
    appealState: z.string().optional(),
    outcome: z.string().optional(),
    answered: z.boolean().optional(),
    responseRequired: z.boolean().optional(),
  })
  .strict()
const e2eResetResponseSchema = z.object({ ok: z.literal(true) }).strict()
const authRefreshRequestSchema = z.object({ refreshToken: z.string().min(1) }).strict()

const moderatorUserId = '22222222-2222-4222-8222-222222222222'
const submissionId = '33333333-3333-4333-8333-333333333333'
const appealSubmissionId = '44444444-4444-4444-8444-444444444444'
const secondSubmissionId = '33333333-3333-4333-8333-333333333334'
const reportId = '55555555-5555-4555-8555-555555555555'
const findingId = '66666666-6666-4666-8666-666666666666'
const coverageRequestId = '99999999-9999-4999-8999-999999999999'
const contentHash = 'a'.repeat(64)
const tupleDigest = 'b'.repeat(64)

const journey = {
  name: 'smoke',
  role: 'User',
  reviewerExcluded: false,
  state: 'DRAFT',
  kind: 'STANDARD',
  caseRevision: 1,
  activeSubmission: null,
  appeal: null,
  answered: false,
  responseRequired: true,
  outcome: null,
  staleDecision: false,
  decisionBlocker: false,
  coverageLevel: 'FULL',
  coverageAssessed: false,
  workspaceRevision: 1,
  evidenceRevision: 0,
  notificationRead: false,
  publicVersionId: null,
}
let versionJourneys = new Map()

function resetJourney(body = {}) {
  journey.name = typeof body.scenario === 'string' ? body.scenario : 'smoke'
  journey.role = body.role === 'Moderator' ? 'Moderator' : 'User'
  journey.reviewerExcluded = journey.name === 'appeal-excluded'
  journey.state = body.state ?? (journey.role === 'Moderator' ? 'SUBMITTED' : 'DRAFT')
  journey.kind = body.kind === 'APPEAL' ? 'APPEAL' : 'STANDARD'
  journey.caseRevision = 1
  journey.activeSubmission = null
  journey.appeal = ['PENDING_REVIEW', 'UPHELD', 'APPROVED', 'WITHDRAWN'].includes(body.appealState)
    ? { state: body.appealState }
    : null
  journey.answered = body.answered === true
  journey.responseRequired = body.responseRequired !== false
  journey.outcome = ['CHANGES_REQUESTED', 'REJECTED'].includes(body.outcome) ? body.outcome : null
  journey.staleDecision = journey.name === 'stale-review'
  journey.decisionBlocker = journey.name === 'decision-blocked'
  journey.coverageLevel = journey.name === 'coverage' ? 'PARTIAL' : 'FULL'
  journey.coverageAssessed = false
  journey.workspaceRevision = 1
  journey.evidenceRevision = body.answered === true ? 1 : 0
  journey.notificationRead = false
  journey.publicVersionId = null
  mutationReceipts.clear()
  versionJourneys = new Map()
  if (journey.name === 'approved-v1-pending-v2') {
    versionJourneys.set(versionId, {
      state: 'APPROVED',
      activeSubmission: null,
      history: [
        {
          submissionId,
          kind: 'STANDARD',
          state: 'APPROVED',
          decisionOutcome: 'APPROVED',
          decisionReason: null,
          createdAt: '2026-01-01T00:00:00.000Z',
          decidedAt: '2026-01-02T00:00:00.000Z',
        },
      ],
    })
    versionJourneys.set(secondVersionId, {
      state: 'SUBMITTED',
      activeSubmission: {
        submissionId: secondSubmissionId,
        kind: 'STANDARD',
        state: 'SUBMITTED',
        caseRevision: 1,
      },
      history: [
        {
          submissionId: secondSubmissionId,
          kind: 'STANDARD',
          state: 'SUBMITTED',
          decisionOutcome: null,
          decisionReason: null,
          createdAt: '2026-01-03T00:00:00.000Z',
          decidedAt: null,
        },
      ],
    })
  }
  if (body.activeSubmission === true) {
    journey.activeSubmission = {
      submissionId: journey.kind === 'APPEAL' ? appealSubmissionId : submissionId,
      kind: journey.kind,
      state: journey.state === 'IN_REVIEW' ? 'IN_REVIEW' : 'SUBMITTED',
      caseRevision: journey.caseRevision,
    }
  }
}

function checked(res, status, body, schema, label) {
  const result = schema.safeParse(body)
  if (!result.success) {
    process.stderr.write(
      `Invalid ${label} stub response: ${result.error.issues.map((issue) => issue.path.join('.')).join(', ')}\n`,
    )
    send(res, 500, { title: 'Invalid deterministic fixture response' })
    return
  }
  send(res, status, result.data)
}

async function readValidatedJson(req, res, schema) {
  const result = schema.safeParse(await readJson(req))
  if (!result.success) {
    send(res, 400, { title: 'Invalid request body', code: 'ERR_VALIDATION' })
    return null
  }
  return result.data
}

function beginMutation(res, route, request) {
  const key = `${route}:${request.operationId}`
  const digest = JSON.stringify(request)
  const previous = mutationReceipts.get(key)
  if (previous) {
    if (previous.digest !== digest) {
      send(res, 409, {
        title: 'Operation conflict',
        code: 'ERR_MODERATION_OPERATION_CONFLICT',
      })
      return null
    }
    const replay = { ...previous.body }
    if (Object.hasOwn(replay, 'replayed')) replay.replayed = true
    send(res, previous.status, replay)
    return null
  }

  return (status, body, schema, label) => {
    const result = schema.safeParse(body)
    if (!result.success) {
      process.stderr.write(`Invalid ${label} stub response.\n`)
      send(res, 500, { title: 'Invalid deterministic fixture response' })
      return
    }
    if (status < 400) mutationReceipts.set(key, { digest, status, body: result.data })
    send(res, status, result.data)
  }
}

// Draft-first seller flow state; mutated by the seller endpoints below so the
// smoke test can exercise save → refetch → upload against one workspace.
const workspaceId = '77777777-7777-4777-8777-777777777777'
let workspaceRevision = 1
let declaration = null
let declarationComplete = false

const sellerDetail = {
  id: assetId,
  title: 'Procedural Shader Kit',
  description: 'A test asset',
  price: 19,
  categoryId,
  categoryName: 'Shaders',
  authorId: sessionUser.id,
  authorUsername: 'seller',
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: '2026-01-01T00:00:00.000Z',
  tags: ['unity'],
  latestVersionId: null,
  latestVersionNumber: null,
  currentReadyVersionId: null,
  publicVersionId: null,
  latestProcessingStatus: 'READY',
  latestProcessingUpdatedAt: '2026-01-01T00:00:00.000Z',
  latestProcessingErrorCode: null,
  latestProcessingErrorSummary: null,
}

const draftMaterial = {
  title: 'Procedural Shader Kit',
  description: 'A test asset',
  categoryId,
  tags: ['unity'],
}

function draftSnapshot() {
  return {
    assetId,
    workspaceId,
    workspaceRevision,
    caseRevision: 0,
    material: draftMaterial,
    declaration,
    declarationComplete,
    latestVersionId: null,
    latestVersionNumber: null,
  }
}

function declarationSnapshot() {
  return {
    assetId,
    assetVersionId: null,
    workspaceId,
    workspaceRevision,
    headRevision: workspaceRevision,
    declaration,
    declarationComplete,
  }
}

function sellerAnalysis() {
  return sellerAnalysisSummarySchema.parse({
    coverageReview: null,
    requiredResponseCount: journey.responseRequired ? 1 : 0,
    outstandingResponseCount: journey.responseRequired && !journey.answered ? 1 : 0,
    evidenceRevision: journey.evidenceRevision,
    assetId,
    assetVersionId: versionId,
    lifecycle: 'COMPLETED',
    runId: reportId,
    reportId,
    reportIsCurrent: true,
    coverageLevel: journey.coverageLevel,
    analyzedFileCount: 1,
    omissionCount: journey.coverageLevel === 'FULL' ? 0 : 1,
    findingCount: 1,
    findingDetailsAvailable: true,
    createdAt: '2026-01-01T00:00:00.000Z',
    completedAt: '2026-01-01T00:01:00.000Z',
    errorCode: null,
    errorMessage: null,
    actionHint: null,
    canRetry: false,
    retryExpectation: null,
    canSubmit: journey.answered || !journey.responseRequired,
    reviewEligible: true,
    submissionBlockedReason: null,
    comparisonScope: 'Selected source files from this version only',
    limitations: [
      'Pair verification is disabled by evaluation. Retained findings require human review.',
    ],
    stages: [
      {
        stage: 'VERIFIER',
        applicability: 'OPTIONAL',
        outcome: 'NOT_APPLICABLE',
        errorCode: 'POLICY_DISABLED',
        retryable: false,
        durationMilliseconds: 0,
      },
    ],
  })
}

function sellerReview(forVersionId = versionId) {
  const versionJourney = versionJourneys.get(forVersionId)
  if (versionJourney) {
    return sellerVersionReviewSchema.parse({
      coverageReview: null,
      requiredResponseCount: 0,
      outstandingResponseCount: 0,
      evidenceRevision: 0,
      assetId,
      assetVersionId: forVersionId,
      versionNumber: forVersionId === versionId ? 1 : 2,
      processingStatus: 'READY',
      processingErrorCode: null,
      processingErrorSummary: null,
      analysis: 'ANALYSIS_AVAILABLE',
      moderationState: versionJourney.state,
      publicationEligible: versionJourney.state === 'APPROVED',
      canUpload: true,
      canSubmit: false,
      canAppeal: false,
      appeal: null,
      activeSubmission: versionJourney.activeSubmission,
      history: versionJourney.history,
      blockedReasons: [],
      analysisDetails: sellerAnalysis(),
      decision:
        versionJourney.state === 'APPROVED'
          ? {
              outcome: 'APPROVED',
              reason: null,
              decidedAt: '2026-01-02T00:00:00.000Z',
              publication: 'PUBLISHED',
            }
          : null,
    })
  }
  const history = []
  if (journey.outcome) {
    history.push({
      submissionId,
      kind: 'STANDARD',
      state: journey.outcome,
      decisionOutcome: journey.outcome,
      decisionReason:
        journey.outcome === 'CHANGES_REQUESTED' || journey.outcome === 'REJECTED'
          ? 'Please update the source declaration.'
          : null,
      createdAt: '2026-01-01T00:00:00.000Z',
      decidedAt: '2026-01-02T00:00:00.000Z',
    })
  }
  if (journey.appeal) {
    history.unshift({
      submissionId: appealSubmissionId,
      kind: 'APPEAL',
      state:
        journey.appeal.state === 'PENDING_REVIEW'
          ? 'SUBMITTED'
          : journey.appeal.state === 'UPHELD'
            ? 'REJECTED'
            : journey.appeal.state,
      decisionOutcome:
        journey.appeal.state === 'APPROVED'
          ? 'APPROVED'
          : journey.appeal.state === 'UPHELD'
            ? 'REJECTED'
            : null,
      decisionReason: journey.appeal.state === 'UPHELD' ? 'The rejection remains final.' : null,
      createdAt: '2026-01-03T00:00:00.000Z',
      decidedAt: journey.appeal.state === 'PENDING_REVIEW' ? null : '2026-01-04T00:00:00.000Z',
    })
  }
  const decisionOutcome =
    journey.outcome === 'CHANGES_REQUESTED'
      ? 'CHANGES_REQUESTED'
      : journey.outcome === 'REJECTED'
        ? 'REJECTED'
        : journey.outcome === 'APPROVED'
          ? 'APPROVED'
          : null
  return sellerVersionReviewSchema.parse({
    coverageReview: null,
    requiredResponseCount: journey.responseRequired ? 1 : 0,
    outstandingResponseCount: journey.responseRequired && !journey.answered ? 1 : 0,
    evidenceRevision: journey.evidenceRevision,
    assetId,
    assetVersionId: forVersionId,
    versionNumber: forVersionId === versionId ? 1 : 2,
    processingStatus: 'READY',
    processingErrorCode: null,
    processingErrorSummary: null,
    analysis: 'ANALYSIS_AVAILABLE',
    moderationState: journey.state,
    publicationEligible: journey.state === 'APPROVED',
    canUpload: true,
    canSubmit: !journey.activeSubmission && (journey.answered || !journey.responseRequired),
    canAppeal: journey.outcome === 'REJECTED' && !journey.appeal,
    appeal: journey.appeal
      ? {
          state: journey.appeal.state,
          requestedAt: '2026-01-03T00:00:00.000Z',
          decidedAt: journey.appeal.state === 'PENDING_REVIEW' ? null : '2026-01-04T00:00:00.000Z',
        }
      : null,
    activeSubmission: journey.activeSubmission,
    history,
    blockedReasons:
      journey.responseRequired && !journey.answered ? ['ERR_FINDING_RESPONSE_REQUIRED'] : [],
    analysisDetails: sellerAnalysis(),
    decision: decisionOutcome
      ? {
          outcome: decisionOutcome,
          reason: 'Please update the source declaration.',
          decidedAt: '2026-01-02T00:00:00.000Z',
          publication: null,
        }
      : null,
  })
}

function sellerFindings(forVersionId = versionId) {
  return sellerAnalysisFindingsPageSchema.parse({
    assetId,
    assetVersionId: forVersionId,
    reportId,
    isCurrent: true,
    coverageLevel: journey.coverageLevel,
    items: [
      {
        responseRequired: journey.responseRequired,
        hasResponse: journey.answered,
        id: findingId,
        findingType: 'SIMILARITY_TOKEN',
        ownPath: 'src/main.ts',
        ownExcerpt: 'export function buildAsset() { return input }',
        startByte: 12,
        endByte: 55,
        startLine: 1,
        endLine: 1,
        reason: 'A retained comparison candidate needs human review.',
        requestedAction: 'Explain the source or choose No evidence.',
        referenceKind: 'FOREIGN_PUBLIC',
        publicAttribution: { label: 'Example public source', url: 'https://example.test/source' },
      },
    ],
    totalCount: 1,
    detailsAvailable: true,
    page: 1,
    pageSize: 50,
    totalPages: 1,
  })
}

function sellerResponses() {
  return sellerResponsesPageSchema.parse({
    workspaceId,
    workspaceRevision: journey.workspaceRevision,
    caseRevision: journey.caseRevision,
    evidenceRevision: journey.evidenceRevision,
    reportId,
    requiredCount: journey.responseRequired ? 1 : 0,
    outstandingCount: journey.responseRequired && !journey.answered ? 1 : 0,
    evidenceText: '',
    items: journey.answered
      ? [
          {
            id: '88888888-8888-4888-8888-888888888888',
            assetVersionId: versionId,
            reportId,
            findingId,
            evidenceRevision: journey.evidenceRevision,
            responseType: 'PRIOR_WORK',
            text: 'I wrote this component independently.',
            createdAt: '2026-01-02T00:00:00.000Z',
            actorUserId: sessionUser.id,
            isCurrent: true,
          },
        ]
      : [],
    totalCount: journey.answered ? 1 : 0,
    page: 1,
    pageSize: 50,
    totalPages: journey.answered ? 1 : 0,
  })
}

function moderatorQueue() {
  const items =
    journey.state === 'SUBMITTED' || journey.state === 'IN_REVIEW'
      ? [
          {
            submissionId: journey.kind === 'APPEAL' ? appealSubmissionId : submissionId,
            assetId,
            assetTitle: 'Procedural Shader Kit',
            versionNumber: 1,
            submittedAt: '2026-01-01T00:00:00.000Z',
            state: journey.state,
            kind: journey.kind,
            claimHolderId: journey.state === 'IN_REVIEW' ? moderatorUserId : null,
            claimHolderName: journey.state === 'IN_REVIEW' ? 'reviewer' : null,
            caseRevision: journey.caseRevision,
            coverageLevel: journey.coverageLevel,
            materialFindingCount: 1,
            hasOpenResponses: journey.responseRequired && !journey.answered,
          },
        ]
      : []
  return moderatorQueuePageSchema.parse({ items, nextCursor: null })
}

function moderatorCase() {
  const caseId = journey.kind === 'APPEAL' ? appealSubmissionId : submissionId
  return moderationCaseSummarySchema.parse({
    submissionId: caseId,
    assetId,
    assetVersionId: versionId,
    state: journey.state,
    kind: journey.kind,
    caseRevision: journey.caseRevision,
    contentSha256: contentHash,
    policyVersion: 'policy-v1',
    basis: {
      versionId,
      contentSha256: contentHash,
      analysisReportId: reportId,
      declarationRevision: 1,
      materialMetadataRevision: 1,
      policyVersion: 'policy-v1',
      sellerEvidenceRevision: journey.evidenceRevision,
      caseRevision: journey.caseRevision,
    },
    manualPlanCompletionReferenceRequired: false,
    isClaimedByCaller: journey.state === 'IN_REVIEW' && !journey.reviewerExcluded,
    coverageAssessmentId: journey.coverageAssessed ? coverageRequestId : null,
    retrievalEvidence: {
      reportId,
      scoresAvailability: 'AVAILABLE',
      referenceSnapshotId: null,
      referenceRevision: null,
      coverageLevel: journey.coverageLevel,
      omissionCount: journey.coverageLevel === 'FULL' ? 0 : 1,
      findingOverflow: 0,
      retrieval: null,
      responseRequirementsAvailable: true,
      candidates: [
        {
          findingId,
          findingKey: 'material-source-finding',
          findingType: 'SIMILARITY_TOKEN',
          ownScopePath: 'src/main.ts',
          ownStartByte: 12,
          ownEndByte: 55,
          ownStartLine: 1,
          ownEndLine: 1,
          referenceItemId: 'public-reference-1',
          referenceOrigin: 'PUBLIC',
          referenceSensitivity: 'PUBLIC',
          scoresAvailability: 'AVAILABLE',
          scores: {
            queryFileId: 'src/main.ts',
            queryUnit: 'module',
            channels: 'TOKEN',
            tokenRank: 1,
            tokenScore: 0.84,
            semanticRank: null,
            semanticScore: null,
            material: true,
          },
          responseRequired: journey.responseRequired,
          answered: journey.answered,
          responses: journey.answered
            ? [
                {
                  kind: 'PRIOR_WORK',
                  text: 'I wrote this component independently.',
                  createdAt: '2026-01-02T00:00:00.000Z',
                },
              ]
            : [],
        },
      ],
    },
  })
}

function coverageQueue() {
  const items =
    journey.coverageLevel === 'FULL' || journey.coverageAssessed
      ? []
      : [
          {
            requestId: coverageRequestId,
            assetVersionId: versionId,
            reportId,
            requestRevision: 1,
            tupleDigest,
            createdAt: '2026-01-01T00:00:00.000Z',
          },
        ]
  return coverageQueuePageSchema.parse({
    items,
    page: 1,
    pageSize: 50,
    totalCount: items.length,
    totalPages: items.length ? 1 : 0,
  })
}

function bumpRefresh(token, ok) {
  if (!refreshByToken[token]) {
    refreshByToken[token] = { ok: 0, fail: 0 }
  }
  if (ok) refreshByToken[token].ok += 1
  else refreshByToken[token].fail += 1
}

async function readJson(req) {
  const chunks = []
  for await (const chunk of req) {
    chunks.push(chunk)
  }
  if (chunks.length === 0) return {}
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'))
  } catch {
    return {}
  }
}

async function readRaw(req) {
  const chunks = []
  for await (const chunk of req) chunks.push(chunk)
  return Buffer.concat(chunks).toString('utf8')
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url ?? '/', `http://127.0.0.1:${PORT}`)
  const pathname = url.pathname

  if (req.method === 'OPTIONS') {
    send(res, 204, {})
    return
  }

  if (pathname === '/hubs/notifications/negotiate' && req.method === 'POST') {
    signalRStats.negotiations += 1
    const connectionId = 'playwright-notification-connection'
    signalRConnections.set(connectionId, {
      messages: [],
      pendingPoll: null,
      initialPoll: true,
      handshaken: false,
    })
    send(res, 200, {
      negotiateVersion: 1,
      connectionId,
      connectionToken: connectionId,
      availableTransports: [{ transport: 'LongPolling', transferFormats: ['Text'] }],
    })
    return
  }

  if (pathname === '/hubs/notifications' && url.searchParams.has('id')) {
    const connection = signalRConnections.get(url.searchParams.get('id'))
    if (!connection) {
      send(res, 404, {})
      return
    }
    if (req.method === 'GET') {
      signalRStats.polls += 1
      const message = connection.messages.shift()
      if (message) {
        sendText(res, 200, message)
      } else if (connection.initialPoll) {
        connection.initialPoll = false
        sendText(res, 200, '')
      } else if (connection.pendingPoll) {
        sendText(connection.pendingPoll, 200, '')
        connection.pendingPoll = res
      } else {
        // LongPolling's initial request completes transport setup before the hub handshake.
        connection.pendingPoll = res
        res.on('close', () => {
          if (connection.pendingPoll === res) connection.pendingPoll = null
        })
      }
      return
    }
    if (req.method === 'POST') {
      void readRaw(req).then((message) => {
        signalRStats.transportPosts += 1
        signalRStats.handshakeBodyBytes = Buffer.byteLength(message)
        if (message.includes('"protocol":"json"')) {
          signalRStats.handshakes += 1
          connection.handshaken = true
          sendSignalREvent(connection, '{}\u001e')
        }
        send(res, 200, {})
      })
      return
    }
    if (req.method === 'DELETE') {
      if (connection.pendingPoll) {
        send(connection.pendingPoll, 204, {})
        connection.pendingPoll = null
      }
      signalRConnections.delete(url.searchParams.get('id'))
      send(res, 202, {})
      return
    }
  }

  if (pathname === '/health') {
    send(res, 200, { ok: true })
    return
  }

  if (pathname === '/__e2e/reset' && req.method === 'POST') {
    void readValidatedJson(req, res, e2eResetRequestSchema).then((body) => {
      if (!body) return
      resetJourney(body)
      signalRConnections.clear()
      signalRStats.tokenRequests = 0
      signalRStats.negotiations = 0
      signalRStats.polls = 0
      signalRStats.transportPosts = 0
      signalRStats.handshakeBodyBytes = 0
      signalRStats.handshakes = 0
      checked(res, 200, { ok: true }, e2eResetResponseSchema, 'reset')
    })
    return
  }

  if (pathname === '/__e2e/role' && req.method === 'POST') {
    void readJson(req).then((body) => {
      if (body.role !== 'User' && body.role !== 'Moderator') {
        send(res, 400, { error: 'Invalid test role.' })
        return
      }
      journey.role = body.role
      send(res, 200, { ok: true })
    })
    return
  }

  if (pathname === '/__e2e/state' && req.method === 'POST') {
    void readJson(req).then((body) => {
      if (body.state !== 'APPROVED') {
        send(res, 400, { error: 'Unsupported test state.' })
        return
      }
      journey.state = body.state
      journey.outcome = body.state
      journey.publicVersionId = versionId
      journey.activeSubmission = null
      send(res, 200, { ok: true })
    })
    return
  }

  if (pathname === '/__e2e/event' && req.method === 'POST') {
    void readJson(req).then((body) => {
      const parsed = moderationCaseChangedMessageSchema.safeParse(body)
      if (!parsed.success) {
        send(res, 400, { error: 'Invalid moderation notification fixture.' })
        return
      }
      journey.state = parsed.data.state
      journey.caseRevision = parsed.data.revision
      if (journey.activeSubmission) {
        journey.activeSubmission.state = parsed.data.state
        journey.activeSubmission.caseRevision = parsed.data.revision
      }
      const event = `${JSON.stringify({
        type: 1,
        target: 'ModerationCaseChanged',
        arguments: [parsed.data],
      })}\u001e`
      for (const connection of signalRConnections.values()) sendSignalREvent(connection, event)
      send(res, 204, {})
    })
    return
  }

  if (pathname === '/__e2e/signalr' && req.method === 'GET') {
    send(res, 200, {
      connected: [...signalRConnections.values()].filter((connection) => connection.handshaken)
        .length,
      ...signalRStats,
    })
    return
  }

  if (pathname === '/api/payments/capabilities') {
    send(res, 200, { checkoutConfigured: false })
    return
  }

  if (pathname === '/debug/auth') {
    const token = url.searchParams.get('token') ?? ''
    send(res, 200, refreshByToken[token] ?? { ok: 0, fail: 0 })
    return
  }

  if (pathname === '/api/auth/signalr-access' && req.method === 'GET') {
    send(res, 200, { hubToken: 'playwright-hub-token' })
    return
  }

  if (pathname === '/api/auth/signalr-token' && req.method === 'POST') {
    signalRStats.tokenRequests += 1
    send(res, 200, { hubToken: 'playwright-hub-token', expiresAt: '2026-01-04T00:00:00Z' })
    return
  }

  if (pathname === '/api/auth/refresh' && req.method === 'POST') {
    void readValidatedJson(req, res, authRefreshRequestSchema).then((body) => {
      if (!body) return
      const token = typeof body.refreshToken === 'string' ? body.refreshToken : ''
      if (token === 'playwright-refresh') {
        bumpRefresh(token, true)
        const now = Date.now()
        checked(
          res,
          200,
          {
            accessToken: ACCESS_OK,
            refreshToken: 'playwright-refresh',
            accessExpiresAt: new Date(now + 15 * 60 * 1000).toISOString(),
            refreshExpiresAt: new Date(now + 7 * 24 * 60 * 60 * 1000).toISOString(),
          },
          tokensResponseSchema,
          'auth refresh',
        )
        return
      }
      bumpRefresh(token || 'missing', false)
      send(res, 401, { title: 'Unauthorized' })
    })
    return
  }

  if (pathname === '/api/users/me') {
    const auth = req.headers.authorization ?? ''
    if (auth.startsWith('Bearer ') && auth.length > 'Bearer '.length) {
      send(
        res,
        200,
        journey.role === 'Moderator'
          ? {
              ...sessionUser,
              id: moderatorUserId,
              username: 'reviewer',
              role: 'Moderator',
            }
          : sessionUser,
      )
      return
    }
    send(res, 401, { title: 'Unauthorized' })
    return
  }

  if (pathname === '/api/users/me/notifications' && req.method === 'GET') {
    const unreadOnly = url.searchParams.get('unreadOnly') === 'true'
    const notification = {
      id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaab',
      kind: 'MODERATION_CHANGES_REQUESTED',
      metadataJson: JSON.stringify({ assetId, submissionId, assetTitle: 'Procedural Shader Kit' }),
      createdAt: '2026-01-02T00:00:00.000Z',
      readAt: journey.notificationRead ? '2026-01-03T00:00:00.000Z' : null,
    }
    const items = unreadOnly && journey.notificationRead ? [] : [notification]
    checked(
      res,
      200,
      {
        items,
        totalCount: unreadOnly ? items.length : 1,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 4),
        totalPages: 1,
      },
      pagedNotificationsSchema,
      'notifications',
    )
    return
  }

  const notificationReadMatch = pathname.match(
    /^\/api\/users\/me\/notifications\/[0-9a-f-]+\/read$/i,
  )
  if (notificationReadMatch && req.method === 'PATCH') {
    journey.notificationRead = true
    send(res, 204, {})
    return
  }

  if (pathname === '/api/users/me/assets/' + assetId && req.method === 'GET') {
    checked(
      res,
      200,
      {
        ...sellerDetail,
        latestVersionId: journey.name === 'approved-v1-pending-v2' ? secondVersionId : versionId,
        latestVersionNumber: journey.name === 'approved-v1-pending-v2' ? 2 : 1,
        currentReadyVersionId: versionId,
        publicVersionId: journey.name === 'approved-v1-pending-v2' ? versionId : null,
      },
      sellerAssetDetailSchema,
      'seller asset detail',
    )
    return
  }

  const reviewMatch = pathname.match(/^\/api\/users\/me\/asset-versions\/([0-9a-f-]+)\/review$/i)
  if (reviewMatch && req.method === 'GET') {
    const selectedVersionId = reviewMatch[1]
    checked(res, 200, sellerReview(selectedVersionId), sellerVersionReviewSchema, 'seller review')
    return
  }

  if (pathname === `/api/users/me/assets/${assetId}/versions/${versionId}/analysis`) {
    checked(res, 200, sellerAnalysis(), sellerAnalysisSummarySchema, 'seller analysis')
    return
  }

  const findingsMatch = pathname.match(
    /^\/api\/users\/me\/asset-versions\/([0-9a-f-]+)\/analysis\/reports\/([0-9a-f-]+)\/findings$/i,
  )
  if (findingsMatch) {
    checked(
      res,
      200,
      sellerFindings(findingsMatch[1]),
      sellerAnalysisFindingsPageSchema,
      'seller findings',
    )
    return
  }

  const responsesMatch = pathname.match(
    new RegExp(`^/api/assets/${assetId}/versions/([0-9a-f-]+)/responses$`, 'i'),
  )
  if (responsesMatch && req.method === 'GET') {
    checked(res, 200, sellerResponses(), sellerResponsesPageSchema, 'seller responses')
    return
  }

  if (responsesMatch && req.method === 'POST') {
    void readValidatedJson(req, res, sellerResponseRequestSchema).then((body) => {
      if (!body) return
      const respond = beginMutation(res, pathname, body)
      if (!respond) return
      journey.answered = true
      journey.evidenceRevision += 1
      respond(
        200,
        {
          responseId: '88888888-8888-4888-8888-888888888888',
          workspaceId,
          workspaceRevision: journey.workspaceRevision,
          caseRevision: journey.caseRevision,
          evidenceRevision: journey.evidenceRevision,
          replayed: false,
        },
        sellerEvidenceSavedSchema,
        'seller response saved',
      )
    })
    return
  }

  const submissionMatch = pathname.match(
    new RegExp(`^/api/assets/${assetId}/versions/([0-9a-f-]+)/submissions$`, 'i'),
  )
  if (submissionMatch && req.method === 'POST') {
    void readValidatedJson(req, res, assetVersionSubmissionRequestSchema).then((body) => {
      if (!body) return
      const respond = beginMutation(res, pathname, body)
      if (!respond) return
      journey.state = 'SUBMITTED'
      journey.kind = 'STANDARD'
      journey.activeSubmission = {
        submissionId,
        kind: 'STANDARD',
        state: 'SUBMITTED',
        caseRevision: ++journey.caseRevision,
      }
      respond(200, { submissionId }, sellerSubmissionCreatedSchema, 'submission created')
    })
    return
  }

  const appealMatch = pathname.match(
    new RegExp(`^/api/assets/${assetId}/versions/([0-9a-f-]+)/appeals$`, 'i'),
  )
  if (appealMatch && req.method === 'POST') {
    void readValidatedJson(req, res, appealRequestSchema).then((body) => {
      if (!body) return
      const respond = beginMutation(res, pathname, body)
      if (!respond) return
      journey.kind = 'APPEAL'
      journey.appeal = { state: 'PENDING_REVIEW' }
      journey.state = 'SUBMITTED'
      journey.activeSubmission = {
        submissionId: appealSubmissionId,
        kind: 'APPEAL',
        state: 'SUBMITTED',
        caseRevision: ++journey.caseRevision,
      }
      respond(
        200,
        { submissionId: appealSubmissionId },
        sellerSubmissionCreatedSchema,
        'appeal created',
      )
    })
    return
  }

  const withdrawMatch = pathname.match(/^\/api\/submissions\/([0-9a-f-]+)\/withdraw$/i)
  if (withdrawMatch && req.method === 'POST') {
    void readValidatedJson(req, res, submissionWithdrawRequestSchema).then((body) => {
      if (!body) return
      const respond = beginMutation(res, pathname, body)
      if (!respond) return
      const requestedSubmissionId = withdrawMatch[1].toLowerCase()
      let selectedVersionJourney = null
      for (const versionJourney of versionJourneys.values()) {
        if (versionJourney.activeSubmission?.submissionId.toLowerCase() === requestedSubmissionId) {
          selectedVersionJourney = versionJourney
          break
        }
      }
      const active = selectedVersionJourney?.activeSubmission ?? journey.activeSubmission
      if (!active || active.submissionId.toLowerCase() !== requestedSubmissionId) {
        send(res, 404, { title: 'Submission not found', code: 'ERR_MODERATION_CASE_NOT_FOUND' })
        return
      }
      if (body.expectedCaseRevision !== active.caseRevision) {
        send(res, 409, { title: 'Submission changed', code: 'ERR_SUBMISSION_WITHDRAW_CONFLICT' })
        return
      }

      const nextCaseRevision = active.caseRevision + 1
      if (selectedVersionJourney) {
        selectedVersionJourney.state = 'WITHDRAWN'
        selectedVersionJourney.activeSubmission = null
        selectedVersionJourney.history = selectedVersionJourney.history.map((entry) =>
          entry.submissionId.toLowerCase() === requestedSubmissionId
            ? { ...entry, state: 'WITHDRAWN' }
            : entry,
        )
      } else {
        journey.state = 'WITHDRAWN'
        journey.activeSubmission = null
        if (journey.appeal) journey.appeal = { state: 'WITHDRAWN' }
      }
      respond(
        200,
        {
          status: 'SUCCEEDED',
          caseRevision: selectedVersionJourney ? nextCaseRevision : ++journey.caseRevision,
          idempotentResultJson: null,
          replayed: false,
        },
        sellerSubmissionWithdrawnSchema,
        'seller submission withdrawn',
      )
    })
    return
  }

  if (pathname === '/api/moderation/submissions' && req.method === 'GET') {
    checked(res, 200, moderatorQueue(), moderatorQueuePageSchema, 'moderator queue')
    return
  }

  const moderationMatch = pathname.match(
    /^\/api\/moderation\/submissions\/([0-9a-f-]+)(?:\/(claim|release|decision))?$/i,
  )
  if (moderationMatch) {
    const id = moderationMatch[1]
    const action = moderationMatch[2]
    if (!action && req.method === 'GET') {
      if (journey.name === 'self-owned-case') {
        send(res, 403, { title: 'Forbidden', code: 'ERR_FORBIDDEN' })
        return
      }
      checked(res, 200, moderatorCase(), moderationCaseSummarySchema, 'moderator case')
      return
    }
    if ((action === 'claim' || action === 'release') && req.method === 'POST') {
      void readValidatedJson(req, res, claimChangeRequestSchema).then((body) => {
        if (!body) return
        const respond = beginMutation(res, pathname, body)
        if (!respond) return
        if (action === 'claim' && journey.name === 'appeal-excluded') {
          send(res, 403, {
            title: 'Appeal reviewer excluded',
            code: 'ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED',
          })
          return
        }
        journey.state = action === 'claim' ? 'IN_REVIEW' : 'SUBMITTED'
        journey.activeSubmission =
          action === 'claim'
            ? {
                submissionId: id,
                kind: journey.kind,
                state: 'IN_REVIEW',
                caseRevision: ++journey.caseRevision,
              }
            : null
        respond(
          200,
          {
            submissionId: id,
            state: journey.state,
            holderId: action === 'claim' ? moderatorUserId : null,
            caseRevision: journey.caseRevision,
            replayed: false,
          },
          claimChangeResponseSchema,
          'claim change',
        )
      })
      return
    }
    if (action === 'decision' && req.method === 'POST') {
      void readValidatedJson(req, res, moderationDecisionRequestSchema).then((body) => {
        if (!body) return
        const respond = beginMutation(res, pathname, body)
        if (!respond) return
        if (journey.name === 'decision-session-expired') {
          send(res, 401, { title: 'Session expired', code: 'ERR_AUTH_TOKEN_EXPIRED' })
          return
        }
        if (journey.staleDecision) {
          journey.caseRevision += 1
          send(res, 409, { title: 'Case changed', code: 'ERR_MODERATION_CASE_STALE' })
          return
        }
        if (journey.name === 'appeal-excluded') {
          send(res, 403, {
            title: 'Appeal reviewer excluded',
            code: 'ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED',
          })
          return
        }
        if (journey.decisionBlocker) {
          send(res, 409, {
            title: 'Decision blocked',
            code: 'ERR_MODERATION_DECISION_RESPONSES_OUTSTANDING',
            blockerCodes: ['ERR_MODERATION_DECISION_RESPONSES_OUTSTANDING'],
          })
          return
        }
        journey.outcome =
          body.outcome === 'REQUEST_CHANGES'
            ? 'CHANGES_REQUESTED'
            : body.outcome === 'APPROVE'
              ? 'APPROVED'
              : body.outcome
        journey.state = journey.outcome
        if (journey.outcome === 'APPROVED') journey.publicVersionId = versionId
        respond(
          200,
          {
            decisionId: '77777777-7777-4777-8777-777777777778',
            state: journey.state,
            publicationSnapshotId: null,
            promoted: false,
            caseRevision: ++journey.caseRevision,
            replayed: false,
          },
          moderationDecisionResponseSchema,
          'decision',
        )
      })
      return
    }
  }

  if (pathname === '/api/moderation/coverage-reviews' && req.method === 'GET') {
    checked(res, 200, coverageQueue(), coverageQueuePageSchema, 'coverage queue')
    return
  }

  if (
    /^\/api\/moderation\/coverage-reviews\/[0-9a-f-]+\/assessments$/i.test(pathname) &&
    req.method === 'POST'
  ) {
    void readValidatedJson(req, res, coverageAssessmentRequestSchema).then((body) => {
      if (!body) return
      const respond = beginMutation(res, pathname, body)
      if (!respond) return
      journey.coverageAssessed = true
      respond(
        200,
        {
          requestId: coverageRequestId,
          requestRevision: 1,
          workspaceRevision: journey.workspaceRevision,
          tupleDigest,
          replayed: false,
        },
        coverageAssessmentResponseSchema,
        'coverage assessment',
      )
    })
    return
  }

  const evidenceMatch = pathname.match(
    /^\/api\/moderation\/submissions\/([0-9a-f-]+)\/evidence\/([A-Z_]+)$/i,
  )
  if (evidenceMatch && req.method === 'GET') {
    checked(
      res,
      200,
      {
        scopeId: '88888888-8888-4888-8888-888888888890',
        reportId,
        kind: evidenceMatch[2],
        findingId: null,
        content: 'Fixture evidence for explicit human review.',
        itemCount: 1,
        truncated: false,
      },
      restrictedEvidenceResponseSchema,
      'restricted evidence',
    )
    return
  }

  if (pathname === `/api/assets/${assetId}`) {
    if (['moderator-approval'].includes(journey.name) && journey.publicVersionId !== versionId) {
      send(res, 404, { title: 'Not found', code: 'ERR_ASSET_NOT_FOUND' })
      return
    }
    send(res, 200, assetDetail)
    return
  }

  if (pathname === `/api/assets/${assetId}/versions` && req.method === 'GET') {
    checked(
      res,
      200,
      [
        {
          id: versionId,
          versionNumber: 1,
          isCurrent: journey.name !== 'approved-v1-pending-v2',
          fileName: 'pack-v1.zip',
          contentLength: 128,
          contentSha256: contentHash,
          releaseNotes: '',
          createdAt: '2026-01-01T00:00:00.000Z',
          license,
          processingStatus: 'READY',
        },
        ...(journey.name === 'approved-v1-pending-v2'
          ? [
              {
                id: secondVersionId,
                versionNumber: 2,
                isCurrent: true,
                fileName: 'pack-v2.zip',
                contentLength: 129,
                contentSha256: 'c'.repeat(64),
                releaseNotes: 'Pending correction',
                createdAt: '2026-01-02T00:00:00.000Z',
                license,
                processingStatus: 'READY',
              },
            ]
          : []),
      ],
      assetVersionsResponseSchema,
      'asset versions',
    )
    return
  }

  if (pathname === `/api/users/me/assets/${assetId}`) {
    checked(res, 200, sellerDetail, sellerAssetDetailSchema, 'seller asset detail')
    return
  }

  if (pathname === `/api/users/me/assets/${assetId}/processing-jobs`) {
    checked(res, 200, [], z.array(assetProcessingJobSchema), 'processing jobs')
    return
  }

  if (pathname === '/api/assets/drafts' && req.method === 'POST') {
    void readValidatedJson(req, res, sellerDraftCreateRequestSchema).then((body) => {
      if (!body) return
      workspaceRevision = 1
      declaration = null
      declarationComplete = false
      checked(
        res,
        200,
        { assetId, workspaceId, workspaceRevision, replayed: false },
        sellerDraftCreatedSchema,
        'draft created',
      )
    })
    return
  }

  if (pathname === `/api/assets/${assetId}/draft` && req.method === 'GET') {
    checked(res, 200, draftSnapshot(), sellerDraftSnapshotSchema, 'draft snapshot')
    return
  }

  if (pathname === `/api/assets/${assetId}/draft` && req.method === 'PATCH') {
    void readValidatedJson(req, res, sellerDraftSaveRequestSchema).then((body) => {
      if (!body) return
      workspaceRevision += 1
      checked(
        res,
        200,
        { workspaceId, workspaceRevision, headRevision: workspaceRevision, replayed: false },
        draftSaveResultSchema,
        'draft saved',
      )
    })
    return
  }

  if (pathname === `/api/assets/${assetId}/declaration` && req.method === 'GET') {
    checked(
      res,
      200,
      declarationSnapshot(),
      sellerDeclarationSnapshotSchema,
      'declaration snapshot',
    )
    return
  }

  if (pathname === `/api/assets/${assetId}/declaration` && req.method === 'PUT') {
    void readValidatedJson(req, res, sellerDeclarationSaveRequestSchema).then((body) => {
      if (!body) return
      workspaceRevision += 1
      declaration = body?.declaration ?? null
      const summary =
        typeof declaration?.ownContributionSummary === 'string'
          ? declaration.ownContributionSummary.trim()
          : ''
      declarationComplete = Boolean(declaration?.redistributionAcknowledged) && summary.length > 0
      checked(
        res,
        200,
        { workspaceId, workspaceRevision, headRevision: workspaceRevision, replayed: false },
        draftSaveResultSchema,
        'declaration saved',
      )
    })
    return
  }

  if (pathname === '/api/assets/upload' && req.method === 'POST') {
    void (async () => {
      try {
        const request = new Request(`http://127.0.0.1:${PORT}${pathname}`, {
          method: 'POST',
          headers: req.headers,
          body: req,
          duplex: 'half',
        })
        const form = await request.formData()
        const metadata = parseAssetUploadMetadata(form)
        const file = form.get('file')
        const filename = file instanceof File ? file.name : null
        if (!metadata.success || validateArchiveUploadFilename(filename)) {
          send(res, 400, { title: 'Invalid upload metadata', code: 'ERR_VALIDATION_FAILED' })
          return
        }
        send(res, 200, { id: assetId })
      } catch {
        send(res, 400, { title: 'Invalid multipart request', code: 'ERR_VALIDATION_FAILED' })
      }
    })()
    return
  }

  if (pathname === `/api/assets/${assetId}/versions` && req.method === 'POST') {
    // Multipart upload — drain the body and acknowledge the created version.
    void (async () => {
      try {
        const request = new Request(`http://127.0.0.1:${PORT}${pathname}`, {
          method: 'POST',
          headers: req.headers,
          body: req,
          duplex: 'half',
        })
        const form = await request.formData()
        const metadata = parsePublishVersionMetadata(form)
        const file = form.get('file')
        const filename = file instanceof File ? file.name : null
        if (!metadata.success || validateArchiveUploadFilename(filename)) {
          send(res, 400, { title: 'Invalid upload metadata', code: 'ERR_VALIDATION_FAILED' })
          return
        }
        send(res, 200, { id: versionId, versionNumber: 1, processingStatus: 'PENDING_INSPECTION' })
      } catch {
        send(res, 400, { title: 'Invalid multipart request', code: 'ERR_VALIDATION_FAILED' })
      }
    })()
    return
  }

  if (pathname.startsWith('/api/reviews/')) {
    send(res, 200, { items: [], totalCount: 0, page: 1, pageSize: 50 })
    return
  }

  if (pathname === '/api/assets') {
    const items = url.searchParams.get('search') === 'shader' ? [shaderSearchItem] : [assetListItem]
    send(res, 200, { items, totalCount: items.length, page: 1, pageSize: 12 })
    return
  }

  if (pathname === '/api/categories') {
    send(res, 200, {
      items: [{ id: categoryId, name: 'Shaders', slug: 'shaders', description: null }],
      totalCount: 1,
      page: 1,
      pageSize: 100,
    })
    return
  }

  if (pathname === '/api/tags') {
    send(res, 200, {
      items: [{ id: '11111111-1111-4111-8111-111111111111', name: 'unity' }],
      totalCount: 1,
      page: 1,
      pageSize: 100,
    })
    return
  }

  if (pathname === `/api/collections/${collectionId}`) {
    send(res, 200, collectionDetail)
    return
  }

  if (pathname === '/api/collections') {
    send(res, 200, {
      items: [
        {
          id: collectionId,
          title: 'Starter Collection',
          description: null,
          status: 'PUBLISHED',
          publishedAt: '2026-01-01T00:00:00.000Z',
          createdAt: '2026-01-01T00:00:00.000Z',
          sellerId: authorId,
          sellerUsername: 'maya',
          itemCount: 1,
          coverAssetId: assetId,
          coverAssetTitle: assetListItem.title,
        },
      ],
      totalCount: 1,
      page: 1,
      pageSize: 12,
    })
    return
  }

  send(res, 404, { title: 'Not found' })
})

let shuttingDown = false
const shutdown = () => {
  if (shuttingDown) return
  shuttingDown = true
  process.stdin.pause()
  if (!server.listening) return
  server.close(() => {
    process.exitCode = 0
  })
  server.closeAllConnections()
}

process.stdin.resume()
process.stdin.once('end', shutdown)
process.stdin.once('close', shutdown)
process.once('SIGINT', shutdown)
process.once('SIGTERM', shutdown)

server.listen(PORT, '127.0.0.1', () => {
  process.stdout.write(`playwright stub API listening on 127.0.0.1:${PORT}\n`)
})
