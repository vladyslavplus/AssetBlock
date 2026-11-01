import { z } from 'zod'

const uuidSchema = z.string().uuid()
const nullableUuidSchema = uuidSchema.nullable()
const moderationStateSchema = z.enum([
  'DRAFT',
  'SUBMITTED',
  'IN_REVIEW',
  'CHANGES_REQUESTED',
  'APPROVED',
  'REJECTED',
  'SUPERSEDED',
  'WITHDRAWN',
])
const moderationSubmissionKindSchema = z.enum(['STANDARD', 'APPEAL'])
const decisionBasisSchema = z
  .object({
    versionId: uuidSchema,
    contentSha256: z.string().regex(/^[a-f0-9]{64}$/),
    analysisReportId: uuidSchema,
    declarationRevision: z.number().int().positive(),
    materialMetadataRevision: z.number().int().positive(),
    policyVersion: z.string().min(1).max(64),
    sellerEvidenceRevision: z.number().int().nonnegative(),
    caseRevision: z.number().int().positive(),
  })
  .strict()
export const restrictedEvidenceKindSchema = z.enum([
  'OWN_EXCERPT',
  'FINDING_PROVENANCE',
  'DECLARATION',
  'MATERIAL_METADATA',
  'SELLER_EVIDENCE',
  'COVERAGE_INVENTORY',
  'ARCHIVE_README',
])

export const moderationQueueQuerySchema = z
  .object({
    state: z.enum(['SUBMITTED', 'IN_REVIEW']).optional(),
    claimFilter: z.enum(['ALL', 'MINE', 'UNCLAIMED']).default('ALL'),
    pageSize: z.coerce.number().int().min(1).max(100).default(50),
    cursor: z.string().min(1).max(2048).optional(),
  })
  .strict()

export const moderatorQueueItemSchema = z
  .object({
    submissionId: uuidSchema,
    assetId: uuidSchema,
    assetTitle: z.string(),
    versionNumber: z.number().int().positive(),
    submittedAt: z.string().datetime({ offset: true }),
    state: moderationStateSchema,
    kind: moderationSubmissionKindSchema,
    claimHolderId: nullableUuidSchema,
    claimHolderName: z.string().nullable(),
    caseRevision: z.number().int().positive(),
    coverageLevel: z.enum(['FULL', 'PARTIAL', 'UNSUPPORTED']).nullable(),
    materialFindingCount: z.number().int().nonnegative(),
    hasOpenResponses: z.boolean(),
  })
  .strict()

export const moderatorQueuePageSchema = z
  .object({ items: z.array(moderatorQueueItemSchema), nextCursor: z.string().nullable() })
  .strict()

export const claimChangeRequestSchema = z
  .object({ operationId: uuidSchema, expectedCaseRevision: z.number().int().positive() })
  .strict()

export const claimChangeResponseSchema = z
  .object({
    submissionId: uuidSchema,
    state: moderationStateSchema,
    holderId: nullableUuidSchema,
    caseRevision: z.number().int().positive(),
    replayed: z.boolean(),
  })
  .strict()

const retrievalCandidateScoresSchema = z
  .object({
    queryFileId: z.string(),
    queryUnit: z.string(),
    channels: z.string(),
    tokenRank: z.number().int().positive().nullable(),
    tokenScore: z.number().nullable(),
    semanticRank: z.number().int().positive().nullable(),
    semanticScore: z.number().nullable(),
    material: z.boolean(),
  })
  .strict()

const findingTypeSchema = z.enum([
  'SIMILARITY_TOKEN',
  'SIMILARITY_SEMANTIC',
  'LICENSE_UNKNOWN',
  'LICENSE_CONTRADICTION',
  'NOTICE_MISSING',
])

const retrievalQuerySummarySchema = z
  .object({
    queryFileId: z.string(),
    tokenCandidates: z.number().int().nonnegative(),
    tokenRetained: z.number().int().nonnegative(),
    tokenOverflow: z.number().int().nonnegative(),
    semanticCandidates: z.number().int().nonnegative(),
    semanticRetained: z.number().int().nonnegative(),
    semanticOverflow: z.number().int().nonnegative(),
    unionRetained: z.number().int().nonnegative(),
    reportOmitted: z.number().int().nonnegative(),
    reportOmittedToken: z.number().int().nonnegative(),
    reportOmittedSemantic: z.number().int().nonnegative(),
    tokenMaxScore: z.number().nullable(),
  })
  .strict()

const retrievalReportSummarySchema = z
  .object({
    tokenTop: z.number().int().nonnegative(),
    semanticTop: z.number().int().nonnegative(),
    unionBudget: z.number().int().nonnegative(),
    queries: z.array(retrievalQuerySummarySchema),
  })
  .strict()

const moderatorCandidateSchema = z
  .object({
    findingId: uuidSchema,
    findingKey: z.string(),
    findingType: findingTypeSchema,
    ownScopePath: z.string(),
    ownStartByte: z.number().int().nonnegative().nullable(),
    ownEndByte: z.number().int().nonnegative().nullable(),
    ownStartLine: z.number().int().positive().nullable(),
    ownEndLine: z.number().int().positive().nullable(),
    referenceItemId: z.string().nullable(),
    referenceOrigin: z.string().nullable(),
    referenceSensitivity: z.string(),
    scoresAvailability: z.string(),
    scores: retrievalCandidateScoresSchema.nullable(),
    responseRequired: z.boolean(),
    answered: z.boolean(),
    responses: z
      .array(
        z
          .object({
            kind: z.enum([
              'PRIOR_WORK',
              'PERMITTED_COMPONENT',
              'INDEPENDENT_IMPLEMENTATION',
              'WRONG_SOURCE',
              'CORRECTED_FILES',
              'NO_EVIDENCE',
            ]),
            text: z.string().max(4000),
            createdAt: z.string().datetime({ offset: true }),
          })
          .strict(),
      )
      .max(200),
  })
  .strict()

const moderatorReportEvidenceSchema = z
  .object({
    reportId: uuidSchema,
    scoresAvailability: z.string(),
    referenceSnapshotId: z.string().nullable(),
    referenceRevision: z.string().nullable(),
    coverageLevel: z.string().nullable(),
    omissionCount: z.number().int().nonnegative().nullable(),
    findingOverflow: z.number().int().nonnegative().nullable(),
    retrieval: retrievalReportSummarySchema.nullable(),
    candidates: z.array(moderatorCandidateSchema),
    responseRequirementsAvailable: z.boolean(),
  })
  .strict()

export const moderationCaseSummarySchema = z
  .object({
    submissionId: uuidSchema,
    assetId: uuidSchema,
    assetVersionId: uuidSchema,
    state: moderationStateSchema,
    kind: moderationSubmissionKindSchema,
    caseRevision: z.number().int().positive(),
    contentSha256: z.string().regex(/^[a-f0-9]{64}$/),
    policyVersion: z.string(),
    basis: decisionBasisSchema,
    manualPlanCompletionReferenceRequired: z.boolean(),
    isClaimedByCaller: z.boolean(),
    retrievalEvidence: moderatorReportEvidenceSchema.nullable(),
    coverageAssessmentId: nullableUuidSchema,
  })
  .strict()

const decisionDispositionSchema = z
  .object({
    findingId: uuidSchema,
    disposition: z.enum([
      'EXPLAINED',
      'FALSE_MATCH',
      'ACCEPTED_WITH_LIMITS',
      'CHANGES_NEEDED',
      'INSUFFICIENT_EVIDENCE',
    ]),
    rationale: z
      .string()
      .max(4000)
      .refine(
        (value) => value.trim().length > 0 && value.trim().length <= 2000 && !value.includes('\0'),
      ),
  })
  .strict()

export const moderationDecisionRequestSchema = z
  .object({
    operationId: uuidSchema,
    basis: decisionBasisSchema,
    outcome: z.enum(['APPROVE', 'REQUEST_CHANGES', 'REJECT']),
    rationale: z
      .string()
      .max(4000)
      .refine(
        (value) => value.trim().length > 0 && value.trim().length <= 2000 && !value.includes('\0'),
      ),
    dispositions: z.array(decisionDispositionSchema).max(200),
    manualPlanCompletionReference: z
      .string()
      .max(400)
      .refine((value) => value.trim().length <= 200 && !value.includes('\0'))
      .nullable()
      .optional(),
  })
  .strict()

export const moderationDecisionResponseSchema = z
  .object({
    decisionId: uuidSchema,
    state: moderationStateSchema,
    publicationSnapshotId: nullableUuidSchema,
    promoted: z.boolean(),
    caseRevision: z.number().int().positive(),
    replayed: z.boolean(),
  })
  .strict()

export const coverageQueueQuerySchema = z
  .object({ page: z.coerce.number().int().min(1).max(10000).default(1) })
  .strict()

export const coverageQueueItemSchema = z
  .object({
    requestId: uuidSchema,
    assetVersionId: uuidSchema,
    reportId: uuidSchema,
    requestRevision: z.number().int().positive(),
    tupleDigest: z.string().regex(/^[a-f0-9]{64}$/),
    createdAt: z.string().datetime({ offset: true }),
  })
  .strict()

export const coverageQueuePageSchema = z
  .object({
    items: z.array(coverageQueueItemSchema),
    page: z.number().int().positive(),
    pageSize: z.number().int().positive(),
    totalCount: z.number().int().nonnegative(),
    totalPages: z.number().int().nonnegative(),
  })
  .strict()

const coverageScopeItemSchema = z
  .object({
    path: z.string().min(1).max(512),
    language: z.string().min(1).max(60),
    roleInCoreLogic: z.string().min(1).max(200),
    readable: z.boolean(),
    evidenceKind: restrictedEvidenceKindSchema,
    findingId: uuidSchema.nullable().optional(),
  })
  .strict()

export const coverageAssessmentRequestSchema = z
  .object({
    operationId: uuidSchema,
    expectedRequestRevision: z.number().int().positive(),
    expectedTupleDigest: z.string().regex(/^[a-f0-9]{64}$/),
    outcome: z.enum(['ACCEPTED', 'BLOCKED']),
    competence: z
      .array(
        z
          .string()
          .min(1)
          .max(60)
          .regex(/^[A-Za-z0-9+#. -]+$/),
      )
      .min(1)
      .max(20),
    scope: z.array(coverageScopeItemSchema).max(20),
    manageableScope: z.boolean(),
    reviewPlan: z.string().min(1).max(2000),
    completedPlanReference: z.string().max(200),
    rationale: z.string().min(1).max(2000),
  })
  .strict()

export const coverageAssessmentResponseSchema = z
  .object({
    requestId: uuidSchema,
    requestRevision: z.number().int().positive(),
    workspaceRevision: z.number().int().positive(),
    tupleDigest: z.string().regex(/^[a-f0-9]{64}$/),
    replayed: z.boolean(),
  })
  .strict()

export const restrictedEvidenceResponseSchema = z
  .object({
    scopeId: uuidSchema,
    reportId: uuidSchema,
    kind: restrictedEvidenceKindSchema,
    findingId: uuidSchema.nullable(),
    content: z.string(),
    itemCount: z.number().int().nonnegative(),
    truncated: z.boolean(),
  })
  .strict()

export type ModeratorQueueItem = z.infer<typeof moderatorQueueItemSchema>
export type ModeratorQueuePage = z.infer<typeof moderatorQueuePageSchema>
export type ModerationCaseSummary = z.infer<typeof moderationCaseSummarySchema>
export type ClaimChangeRequest = z.infer<typeof claimChangeRequestSchema>
export type ModerationDecisionRequest = z.infer<typeof moderationDecisionRequestSchema>
export type CoverageQueueItem = z.infer<typeof coverageQueueItemSchema>
export type CoverageAssessmentRequest = z.infer<typeof coverageAssessmentRequestSchema>
export type RestrictedEvidenceKind = z.infer<typeof restrictedEvidenceKindSchema>
