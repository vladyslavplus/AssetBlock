import { describe, expect, it } from 'vitest'

import {
  SELLER_DECLARATION_MAX_BYTES,
  sellerDeclarationSaveRequestSchema,
  sellerDeclarationSnapshotSchema,
  sellerDraftCreatedSchema,
  sellerDraftCreateRequestSchema,
  sellerDraftSnapshotSchema,
  sellerVersionReviewSchema,
  sourceDeclarationByteSize,
  sourceDeclarationPayloadSchema,
  type SourceDeclarationPayload,
} from '@/lib/seller/seller-draft-schemas'

const COMPONENT = {
  componentId: 'c1',
  name: 'left-pad',
  packagePathOrRange: 'libs/left-pad@1.0.0',
  sourceUrl: 'https://example.com/left-pad',
  knownVersion: '1.0.0',
  license: 'MIT',
  noticeLocations: ['NOTICE.txt'],
  modifications: null,
  permissionEvidenceReferences: [],
  origin: 'THIRD_PARTY' as const,
}

const DECLARATION: SourceDeclarationPayload = {
  ownContributionSummary: 'Authored the pipeline myself.',
  ownChanges: null,
  earlierWork: null,
  redistributionAcknowledged: true,
  disclosurePolicyVersion: '1',
  components: [COMPONENT],
}

describe('sellerDraftSnapshotSchema', () => {
  it('accepts a draft-only snapshot with nullable latest version', () => {
    const result = sellerDraftSnapshotSchema.safeParse({
      assetId: '123e4567-e89b-12d3-a456-426614174000',
      workspaceId: '123e4567-e89b-12d3-a456-426614174001',
      workspaceRevision: 3,
      caseRevision: 1,
      material: {
        title: 'Draft asset',
        description: null,
        categoryId: '123e4567-e89b-12d3-a456-426614174002',
        tags: ['tools'],
      },
      declaration: null,
      declarationComplete: false,
      latestVersionId: null,
      latestVersionNumber: null,
    })

    expect(result.success).toBe(true)
  })

  it('rejects non-null latestVersionId when it is not a uuid', () => {
    const result = sellerDraftSnapshotSchema.safeParse({
      assetId: '123e4567-e89b-12d3-a456-426614174000',
      workspaceId: '123e4567-e89b-12d3-a456-426614174001',
      workspaceRevision: 3,
      caseRevision: 1,
      material: {
        title: 'Draft asset',
        description: null,
        categoryId: '123e4567-e89b-12d3-a456-426614174002',
        tags: [],
      },
      declaration: null,
      declarationComplete: false,
      latestVersionId: 'not-a-uuid',
      latestVersionNumber: 1,
    })

    expect(result.success).toBe(false)
  })
})

describe('sourceDeclarationPayloadSchema', () => {
  it('rejects non-https source URLs', () => {
    const result = sourceDeclarationPayloadSchema.safeParse({
      ...DECLARATION,
      components: [{ ...COMPONENT, sourceUrl: 'http://example.com/left-pad' }],
    })

    expect(result.success).toBe(false)
  })

  it('accepts the UNKNOWN license marker', () => {
    const result = sourceDeclarationPayloadSchema.safeParse({
      ...DECLARATION,
      components: [{ ...COMPONENT, license: 'UNKNOWN' }],
    })

    expect(result.success).toBe(true)
  })
})

describe('sellerDeclarationSaveRequestSchema', () => {
  const base = {
    operationId: '123e4567-e89b-12d3-a456-426614174003',
    expectedWorkspaceRevision: 2,
  }

  it('accepts a small declaration', () => {
    const result = sellerDeclarationSaveRequestSchema.safeParse({
      ...base,
      declaration: DECLARATION,
    })

    expect(result.success).toBe(true)
  })

  it('rejects a declaration over the 256 KiB byte limit', () => {
    const huge: SourceDeclarationPayload = {
      ...DECLARATION,
      components: [
        ...DECLARATION.components,
        {
          ...COMPONENT,
          componentId: 'c2',
          modifications: 'x'.repeat(SELLER_DECLARATION_MAX_BYTES),
        },
      ],
    }
    expect(sourceDeclarationByteSize(huge)).toBeGreaterThan(SELLER_DECLARATION_MAX_BYTES)

    const result = sellerDeclarationSaveRequestSchema.safeParse({
      ...base,
      declaration: huge,
    })

    expect(result.success).toBe(false)
    if (!result.success) {
      expect(result.error.issues[0]?.path).toContain('declaration')
    }
  })
})

describe('sellerVersionReviewSchema', () => {
  it('accepts a review payload with blocked reasons', () => {
    const result = sellerVersionReviewSchema.safeParse({
      assetId: '123e4567-e89b-12d3-a456-426614174000',
      assetVersionId: '123e4567-e89b-12d3-a456-426614174001',
      versionNumber: 1,
      processingStatus: 'PENDING_INSPECTION',
      processingErrorCode: null,
      processingErrorSummary: null,
      analysis: 'ANALYSIS_NOT_AVAILABLE',
      moderationState: 'DRAFT',
      publicationEligible: false,
      canUpload: true,
      canSubmit: false,
      blockedReasons: ['ERR_ANALYSIS_NOT_AVAILABLE'],
    })

    expect(result.success).toBe(true)
  })

  it('rejects numeric enum values instead of canonical strings', () => {
    const result = sellerVersionReviewSchema.safeParse({
      assetId: '123e4567-e89b-12d3-a456-426614174000',
      assetVersionId: '123e4567-e89b-12d3-a456-426614174001',
      versionNumber: 1,
      processingStatus: 0,
      processingErrorCode: null,
      processingErrorSummary: null,
      analysis: 0,
      moderationState: 1,
      publicationEligible: false,
      canUpload: true,
      canSubmit: false,
      blockedReasons: [],
    })

    expect(result.success).toBe(false)
  })
})

describe('backend contract alignment', () => {
  const UUID = '123e4567-e89b-12d3-a456-426614174000'

  it('accepts OWN_CONTRIBUTION and rejects unknown origin values', () => {
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, origin: 'OWN_CONTRIBUTION' }],
      }).success,
    ).toBe(true)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, origin: 'unknown' }],
      }).success,
    ).toBe(false)
  })

  it('rejects null required collections instead of treating them as empty', () => {
    expect(
      sourceDeclarationPayloadSchema.safeParse({ ...DECLARATION, components: null }).success,
    ).toBe(false)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, noticeLocations: null }],
      }).success,
    ).toBe(false)
  })

  it('enforces backend component field limits', () => {
    const boundary = 'x'.repeat(100)
    const over = 'x'.repeat(101)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, componentId: boundary }],
      }).success,
    ).toBe(true)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, componentId: over }],
      }).success,
    ).toBe(false)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, name: 'x'.repeat(200) }],
      }).success,
    ).toBe(true)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, name: 'x'.repeat(201) }],
      }).success,
    ).toBe(false)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, knownVersion: 'x'.repeat(200) }],
      }).success,
    ).toBe(true)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, knownVersion: 'x'.repeat(201) }],
      }).success,
    ).toBe(false)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, noticeLocations: ['x'.repeat(500)] }],
      }).success,
    ).toBe(true)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, noticeLocations: ['x'.repeat(501)] }],
      }).success,
    ).toBe(false)
  })

  it('accepts incomplete declarations with an empty summary', () => {
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        ownContributionSummary: '',
      }).success,
    ).toBe(true)
  })

  it('rejects source URLs that are not absolute HTTPS URIs', () => {
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, sourceUrl: 'https:// not a url' }],
      }).success,
    ).toBe(false)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, sourceUrl: 'not-a-url' }],
      }).success,
    ).toBe(false)
    expect(
      sourceDeclarationPayloadSchema.safeParse({
        ...DECLARATION,
        components: [{ ...COMPONENT, sourceUrl: 'https://' }],
      }).success,
    ).toBe(false)
  })

  it('parses a declaration snapshot that feeds the next save CAS', () => {
    const result = sellerDeclarationSnapshotSchema.safeParse({
      assetId: UUID,
      assetVersionId: null,
      workspaceId: '123e4567-e89b-12d3-a456-426614174001',
      workspaceRevision: 4,
      headRevision: 1,
      declaration: null,
      declarationComplete: false,
    })

    expect(result.success).toBe(true)
  })

  it('requires the replayed flag on draft-created responses', () => {
    expect(
      sellerDraftCreatedSchema.safeParse({
        assetId: UUID,
        workspaceId: '123e4567-e89b-12d3-a456-426614174001',
        workspaceRevision: 1,
        replayed: false,
      }).success,
    ).toBe(true)
    expect(
      sellerDraftCreatedSchema.safeParse({
        assetId: UUID,
        workspaceId: '123e4567-e89b-12d3-a456-426614174001',
        workspaceRevision: 1,
      }).success,
    ).toBe(false)
  })

  it('requires a uuid operationId on draft-create requests (Zod strip would drop it otherwise)', () => {
    expect(
      sellerDraftCreateRequestSchema.safeParse({
        operationId: '123e4567-e89b-12d3-a456-426614174004',
        title: 'Draft',
        description: null,
        price: 10,
        categoryId: UUID,
        downloadLimitPerHour: null,
      }).success,
    ).toBe(true)
    expect(
      sellerDraftCreateRequestSchema.safeParse({
        title: 'Draft',
        description: null,
        price: 10,
        categoryId: UUID,
        downloadLimitPerHour: null,
      }).success,
    ).toBe(false)
    expect(
      sellerDraftCreateRequestSchema.safeParse({
        operationId: 'not-a-uuid',
        title: 'Draft',
        description: null,
        price: 10,
        categoryId: UUID,
        downloadLimitPerHour: null,
      }).success,
    ).toBe(false)
  })
})
