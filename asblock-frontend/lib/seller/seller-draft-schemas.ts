import { z } from 'zod'

export const SELLER_TITLE_MAX = 500
export const SELLER_DESCRIPTION_MAX = 5000
export const SELLER_RELEASE_NOTES_MAX = 4000
export const SELLER_COMPONENTS_MAX = 100
export const SELLER_EXPLANATION_MAX = 4000
export const SELLER_EVIDENCE_REFS_MAX = 20
export const SELLER_NOTICE_LOCATIONS_MAX = 20
export const SELLER_DECLARATION_MAX_BYTES = 256 * 1024
export const SELLER_LICENSE_UNKNOWN = 'UNKNOWN'

export const sellerDraftMaterialPayloadSchema = z.object({
  title: z.string().min(1, 'Title is required').max(SELLER_TITLE_MAX),
  description: z.string().max(SELLER_DESCRIPTION_MAX).nullable(),
  categoryId: z.guid('Select a category'),
  tags: z.array(z.string().min(1)).max(100),
})
export type SellerDraftMaterialPayload = z.infer<typeof sellerDraftMaterialPayloadSchema>

// Component field limits mirror SellerDraftLimits on the backend; keep both sides identical.
export const SELLER_COMPONENT_ID_MAX = 100
export const SELLER_COMPONENT_NAME_MAX = 200
export const SELLER_COMPONENT_PATH_MAX = 200
export const SELLER_COMPONENT_SOURCE_URL_MAX = 2000
export const SELLER_COMPONENT_KNOWN_VERSION_MAX = 200
export const SELLER_COMPONENT_LICENSE_MAX = 200
export const SELLER_COMPONENT_NOTICE_ITEM_MAX = 500
export const SELLER_COMPONENT_REF_ITEM_MAX = 500

export const sourceComponentOriginSchema = z.enum(['OWN_CONTRIBUTION', 'THIRD_PARTY'])
export type SourceComponentOrigin = z.infer<typeof sourceComponentOriginSchema>

export const sourceDeclarationComponentSchema = z.object({
  componentId: z.string().min(1).max(SELLER_COMPONENT_ID_MAX),
  name: z.string().min(1).max(SELLER_COMPONENT_NAME_MAX),
  packagePathOrRange: z.string().max(SELLER_COMPONENT_PATH_MAX).nullable(),
  sourceUrl: z
    .string()
    .max(SELLER_COMPONENT_SOURCE_URL_MAX)
    .url()
    .refine((url) => url.startsWith('https://'), 'Source URL must use HTTPS'),
  knownVersion: z.string().max(SELLER_COMPONENT_KNOWN_VERSION_MAX).nullable(),
  license: z.string().min(1).max(SELLER_COMPONENT_LICENSE_MAX),
  noticeLocations: z
    .array(z.string().max(SELLER_COMPONENT_NOTICE_ITEM_MAX))
    .max(SELLER_NOTICE_LOCATIONS_MAX),
  modifications: z.string().max(SELLER_EXPLANATION_MAX).nullable(),
  permissionEvidenceReferences: z
    .array(z.string().max(SELLER_COMPONENT_REF_ITEM_MAX))
    .max(SELLER_EVIDENCE_REFS_MAX),
  origin: sourceComponentOriginSchema,
})
export type SourceDeclarationComponent = z.infer<typeof sourceDeclarationComponentSchema>

export const sourceDeclarationPayloadSchema = z.object({
  // Incomplete declarations are valid on the backend (empty summary is stored, not rejected).
  ownContributionSummary: z.string().max(SELLER_EXPLANATION_MAX),
  ownChanges: z.string().max(SELLER_EXPLANATION_MAX).nullable(),
  earlierWork: z.string().max(SELLER_EXPLANATION_MAX).nullable(),
  redistributionAcknowledged: z.boolean(),
  disclosurePolicyVersion: z.string().min(1).max(64),
  components: z.array(sourceDeclarationComponentSchema).max(SELLER_COMPONENTS_MAX),
})
export type SourceDeclarationPayload = z.infer<typeof sourceDeclarationPayloadSchema>

/**
 * Serialized size guard mirrored from the backend byte limit.
 */
export function sourceDeclarationByteSize(payload: SourceDeclarationPayload): number {
  return new TextEncoder().encode(JSON.stringify(payload)).length
}

export const sellerDraftSnapshotSchema = z.object({
  assetId: z.guid(),
  workspaceId: z.guid(),
  workspaceRevision: z.number().int().nonnegative(),
  caseRevision: z.number().int().nonnegative(),
  material: sellerDraftMaterialPayloadSchema,
  declaration: sourceDeclarationPayloadSchema.nullable(),
  declarationComplete: z.boolean(),
  latestVersionId: z.guid().nullable(),
  latestVersionNumber: z.number().int().nonnegative().nullable(),
})
export type SellerDraftSnapshot = z.infer<typeof sellerDraftSnapshotSchema>

export const draftSaveResultSchema = z.object({
  workspaceId: z.guid(),
  workspaceRevision: z.number().int().nonnegative(),
  headRevision: z.number().int().nonnegative(),
  replayed: z.boolean(),
})
export type DraftSaveResult = z.infer<typeof draftSaveResultSchema>

export const sellerDraftCreatedSchema = z.object({
  assetId: z.guid(),
  workspaceId: z.guid(),
  workspaceRevision: z.number().int().nonnegative(),
  replayed: z.boolean(),
})
export type SellerDraftCreated = z.infer<typeof sellerDraftCreatedSchema>

/**
 * Typed declaration read result: workspaceId/workspaceRevision feed the next
 * declaration save's CAS; declaration is null when the workspace exists but no
 * declaration revision has been stored yet.
 */
export const sellerDeclarationSnapshotSchema = z.object({
  assetId: z.guid(),
  assetVersionId: z.guid().nullable(),
  workspaceId: z.guid(),
  workspaceRevision: z.number().int().nonnegative(),
  headRevision: z.number().int().nonnegative(),
  declaration: sourceDeclarationPayloadSchema.nullable(),
  declarationComplete: z.boolean(),
})
export type SellerDeclarationSnapshot = z.infer<typeof sellerDeclarationSnapshotSchema>

export const sellerProcessingStateSchema = z.enum([
  'PENDING_INSPECTION',
  'PENDING_MALWARE_SCAN',
  'READY',
  'REJECTED',
  'PROCESSING_FAILED',
])

export const sellerAnalysisAvailabilitySchema = z.enum([
  'ANALYSIS_NOT_AVAILABLE',
  'ANALYSIS_AVAILABLE',
])

export const sellerModerationStateSchema = z.enum([
  'DRAFT',
  'SUBMITTED',
  'IN_REVIEW',
  'CHANGES_REQUESTED',
  'APPROVED',
  'REJECTED',
  'WITHDRAWN',
  'SUPERSEDED',
])

export const sellerAnalysisLifecycleSchema = z.enum([
  'NOT_AVAILABLE',
  'QUEUED',
  'RUNNING',
  'COMPLETED',
  'FAILED',
  'CANCELLED',
])
export const sellerAnalysisStageSchema = z.object({
  stage: z.string().min(1).max(80),
  applicability: z.string().min(1).max(40),
  outcome: z.string().min(1).max(40),
  retryable: z.boolean(),
})
export const sellerAnalysisSummarySchema = z.object({
  requiredResponseCount: z.number().int().nonnegative(),
  outstandingResponseCount: z.number().int().nonnegative(),
  evidenceRevision: z.number().int().nonnegative(),
  assetId: z.guid(),
  assetVersionId: z.guid(),
  lifecycle: sellerAnalysisLifecycleSchema,
  runId: z.guid().nullable(),
  reportId: z.guid().nullable(),
  reportIsCurrent: z.boolean(),
  coverageLevel: z.enum(['FULL', 'PARTIAL', 'UNSUPPORTED']).nullable(),
  analyzedFileCount: z.number().int().nonnegative(),
  omissionCount: z.number().int().nonnegative(),
  findingCount: z.number().int().nonnegative(),
  findingDetailsAvailable: z.boolean(),
  createdAt: z.iso.datetime({ offset: true }).nullable(),
  completedAt: z.iso.datetime({ offset: true }).nullable(),
  errorCode: z.string().max(80).nullable(),
  errorMessage: z.string().max(300).nullable(),
  actionHint: z.string().max(300).nullable(),
  canRetry: z.boolean(),
  retryExpectation: z
    .object({
      runId: z.guid(),
      workspaceRevision: z.number().int().nonnegative(),
      policyDigest: z.string().length(64),
      inputDigest: z.string().length(64),
    })
    .nullable(),
  canSubmit: z.boolean(),
  reviewEligible: z.boolean(),
  submissionBlockedReason: z.string().max(100).nullable(),
  comparisonScope: z.string().max(160),
  limitations: z.array(z.string().max(300)),
  stages: z.array(sellerAnalysisStageSchema),
})
export type SellerAnalysisSummary = z.infer<typeof sellerAnalysisSummarySchema>
export const sellerAnalysisFindingSchema = z.object({
  responseRequired: z.boolean(),
  hasResponse: z.boolean(),
  id: z.guid(),
  findingType: z.string().min(1).max(80),
  ownPath: z.string().min(1).max(1024),
  ownExcerpt: z.string().max(320),
  startByte: z.number().int().nonnegative().nullable(),
  endByte: z.number().int().nonnegative().nullable(),
  startLine: z.number().int().positive().nullable(),
  endLine: z.number().int().positive().nullable(),
  reason: z.string().max(1024),
  requestedAction: z.string().max(512),
  referenceKind: z.enum(['OWN_PRIOR_VERSION', 'FOREIGN_PRIVATE', 'FOREIGN_PUBLIC']).nullable(),
  publicAttribution: z
    .object({
      label: z.string().min(1).max(120),
      url: z
        .string()
        .url()
        .refine((value) => new URL(value).protocol === 'https:')
        .nullable(),
    })
    .nullable(),
})
export const sellerAnalysisFindingsPageSchema = z.object({
  assetId: z.guid(),
  assetVersionId: z.guid(),
  reportId: z.guid(),
  isCurrent: z.boolean(),
  coverageLevel: z.enum(['FULL', 'PARTIAL', 'UNSUPPORTED']),
  items: z.array(sellerAnalysisFindingSchema),
  totalCount: z.number().int().nonnegative(),
  detailsAvailable: z.boolean(),
  page: z.number().int().positive(),
  pageSize: z.number().int().positive().max(50),
  totalPages: z.number().int().nonnegative(),
})
export type SellerAnalysisFindingsPage = z.infer<typeof sellerAnalysisFindingsPageSchema>
export const sellerAnalysisRetryRequestSchema = z.object({
  operationId: z.guid(),
  expectedRunId: z.guid(),
  expectedWorkspaceRevision: z.number().int().positive(),
  expectedPolicyDigest: z.string().length(64),
  expectedInputDigest: z.string().length(64),
})
export const sellerAnalysisRetryAcceptedSchema = z.object({
  operationId: z.guid(),
  assetId: z.guid(),
  assetVersionId: z.guid(),
  status: z.enum(['ACCEPTED', 'RECHECK_QUEUED']),
  runId: z.guid().nullable(),
  jobId: z.guid().nullable(),
  securityRecheckId: z.guid().nullable(),
  replayed: z.boolean(),
})
export type SellerAnalysisRetryRequest = z.infer<typeof sellerAnalysisRetryRequestSchema>
export type SellerAnalysisRetryAccepted = z.infer<typeof sellerAnalysisRetryAcceptedSchema>

export const sellerVersionReviewSchema = z.object({
  requiredResponseCount: z.number().int().nonnegative(),
  outstandingResponseCount: z.number().int().nonnegative(),
  evidenceRevision: z.number().int().nonnegative(),
  assetId: z.guid(),
  assetVersionId: z.guid(),
  versionNumber: z.number().int().nonnegative(),
  processingStatus: sellerProcessingStateSchema,
  processingErrorCode: z.string().min(1).max(64).nullable(),
  processingErrorSummary: z.string().max(4000).nullable(),
  analysis: sellerAnalysisAvailabilitySchema,
  moderationState: sellerModerationStateSchema,
  publicationEligible: z.boolean(),
  canUpload: z.boolean(),
  canSubmit: z.boolean(),
  blockedReasons: z.array(z.string()),
  analysisDetails: sellerAnalysisSummarySchema.nullable().optional(),
})
export type SellerVersionReview = z.infer<typeof sellerVersionReviewSchema>

export const sellerDraftCreateRequestSchema = z.object({
  // One UUID per logical creation; callers keep it to replay lost responses.
  operationId: z.guid(),
  title: z.string().min(1, 'Title is required').max(SELLER_TITLE_MAX),
  description: z.string().max(SELLER_DESCRIPTION_MAX).nullable().optional(),
  price: z.number().nonnegative(),
  categoryId: z.guid('Select a category'),
  downloadLimitPerHour: z.number().int().positive().max(10000).nullable().optional(),
})

export const sellerDraftSaveRequestSchema = z.object({
  operationId: z.guid(),
  expectedWorkspaceRevision: z.number().int().nonnegative(),
  material: sellerDraftMaterialPayloadSchema,
})

export const sellerDeclarationSaveRequestSchema = z
  .object({
    operationId: z.guid(),
    expectedWorkspaceRevision: z.number().int().nonnegative(),
    declaration: sourceDeclarationPayloadSchema,
  })
  .refine(
    (request) => sourceDeclarationByteSize(request.declaration) <= SELLER_DECLARATION_MAX_BYTES,
    { message: 'Source declaration must be at most 256 KiB', path: ['declaration'] },
  )

export const assetPriceUpdateRequestSchema = z.object({
  price: z.number().nonnegative(),
})

export const assetVersionSubmissionRequestSchema = z.object({
  workspaceId: z.guid(),
  expectedWorkspaceRevision: z.number().int().nonnegative(),
  expectedCaseRevision: z.number().int().nonnegative(),
  operationId: z.guid(),
})

export const submissionWithdrawRequestSchema = z.object({
  expectedCaseRevision: z.number().int().nonnegative(),
  operationId: z.guid(),
})

// Evidence revision and text limits mirror the append-only seller evidence contract.
export const SELLER_RESPONSE_TEXT_MAX = 2000
export const sellerResponseTypeSchema = z.enum([
  'PRIOR_WORK',
  'PERMITTED_COMPONENT',
  'INDEPENDENT_IMPLEMENTATION',
  'WRONG_SOURCE',
  'CORRECTED_FILES',
  'NO_EVIDENCE',
])
export const sellerEvidenceEditRequestSchema = z
  .object({
    workspaceId: z.guid(),
    reportId: z.guid(),
    operationId: z.guid(),
    expectedWorkspaceRevision: z.number().int().positive(),
    expectedEvidenceRevision: z.number().int().nonnegative(),
    text: z
      .string()
      .trim()
      .max(SELLER_RESPONSE_TEXT_MAX)
      .refine((value) => !value.includes('\0'), 'Text contains an invalid character.'),
  })
  .strict()
export const sellerResponseRequestSchema = sellerEvidenceEditRequestSchema
  .extend({
    findingId: z.guid(),
    responseType: sellerResponseTypeSchema,
  })
  .refine((value) => value.responseType === 'NO_EVIDENCE' || value.text.length > 0, {
    message: 'Explain your response or choose No evidence.',
    path: ['text'],
  })
export const sellerEvidenceSavedSchema = z.object({
  responseId: z.guid().nullable(),
  workspaceId: z.guid(),
  workspaceRevision: z.number().int().positive(),
  evidenceRevision: z.number().int().positive(),
  caseRevision: z.number().int().nonnegative(),
  replayed: z.boolean(),
})
export const sellerResponsesPageSchema = z.object({
  workspaceId: z.guid(),
  workspaceRevision: z.number().int().positive(),
  caseRevision: z.number().int().nonnegative(),
  evidenceRevision: z.number().int().nonnegative(),
  reportId: z.guid().nullable(),
  requiredCount: z.number().int().nonnegative(),
  outstandingCount: z.number().int().nonnegative(),
  evidenceText: z.string().max(SELLER_RESPONSE_TEXT_MAX),
  items: z.array(
    z.object({
      id: z.guid(),
      assetVersionId: z.guid(),
      reportId: z.guid(),
      findingId: z.guid(),
      evidenceRevision: z.number().int().positive(),
      responseType: sellerResponseTypeSchema,
      text: z.string().max(SELLER_RESPONSE_TEXT_MAX),
      createdAt: z.iso.datetime({ offset: true }),
      actorUserId: z.guid(),
      isCurrent: z.boolean(),
    }),
  ),
  totalCount: z.number().int().nonnegative(),
  page: z.number().int().positive(),
  pageSize: z.number().int().positive().max(50),
  totalPages: z.number().int().nonnegative(),
})
export type SellerResponseRequest = z.infer<typeof sellerResponseRequestSchema>
export type SellerEvidenceEditRequest = z.infer<typeof sellerEvidenceEditRequestSchema>
export type SellerResponsesPage = z.infer<typeof sellerResponsesPageSchema>

export const sellerResponseFormSchema = z
  .object({
    text: sellerEvidenceEditRequestSchema.shape.text,
    responseType: sellerResponseTypeSchema,
  })
  .refine((value) => value.responseType === 'NO_EVIDENCE' || value.text.length > 0, {
    message: 'Explain your response or choose No evidence.',
    path: ['text'],
  })
export type SellerResponseFormValues = z.infer<typeof sellerResponseFormSchema>
