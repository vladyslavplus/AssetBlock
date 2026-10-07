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
  categoryId: z.string().uuid('Select a category'),
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
  assetId: z.string().uuid(),
  workspaceId: z.string().uuid(),
  workspaceRevision: z.number().int().nonnegative(),
  caseRevision: z.number().int().nonnegative(),
  material: sellerDraftMaterialPayloadSchema,
  declaration: sourceDeclarationPayloadSchema.nullable(),
  declarationComplete: z.boolean(),
  latestVersionId: z.string().uuid().nullable(),
  latestVersionNumber: z.number().int().nonnegative().nullable(),
})
export type SellerDraftSnapshot = z.infer<typeof sellerDraftSnapshotSchema>

export const draftSaveResultSchema = z.object({
  workspaceId: z.string().uuid(),
  workspaceRevision: z.number().int().nonnegative(),
  headRevision: z.number().int().nonnegative(),
  replayed: z.boolean(),
})
export type DraftSaveResult = z.infer<typeof draftSaveResultSchema>

export const sellerDraftCreatedSchema = z.object({
  assetId: z.string().uuid(),
  workspaceId: z.string().uuid(),
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
  assetId: z.string().uuid(),
  assetVersionId: z.string().uuid().nullable(),
  workspaceId: z.string().uuid(),
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
])

export const sellerVersionReviewSchema = z.object({
  assetId: z.string().uuid(),
  assetVersionId: z.string().uuid(),
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
})
export type SellerVersionReview = z.infer<typeof sellerVersionReviewSchema>

export const sellerDraftCreateRequestSchema = z.object({
  // One UUID per logical creation; callers keep it to replay lost responses.
  operationId: z.string().uuid(),
  title: z.string().min(1, 'Title is required').max(SELLER_TITLE_MAX),
  description: z.string().max(SELLER_DESCRIPTION_MAX).nullable().optional(),
  price: z.number().nonnegative(),
  categoryId: z.string().uuid('Select a category'),
  downloadLimitPerHour: z.number().int().positive().max(10000).nullable().optional(),
})

export const sellerDraftSaveRequestSchema = z.object({
  operationId: z.string().uuid(),
  expectedWorkspaceRevision: z.number().int().nonnegative(),
  material: sellerDraftMaterialPayloadSchema,
})

export const sellerDeclarationSaveRequestSchema = z
  .object({
    operationId: z.string().uuid(),
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
  workspaceId: z.string().uuid(),
  expectedWorkspaceRevision: z.number().int().nonnegative(),
  expectedCaseRevision: z.number().int().nonnegative(),
  operationId: z.string().uuid(),
})

export const submissionWithdrawRequestSchema = z.object({
  expectedCaseRevision: z.number().int().nonnegative(),
  operationId: z.string().uuid(),
})
