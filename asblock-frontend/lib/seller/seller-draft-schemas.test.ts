import { describe, expect, it } from 'vitest'

import {
  SELLER_DECLARATION_MAX_BYTES,
  sellerDeclarationSaveRequestSchema,
  sellerDeclarationSnapshotSchema,
  sellerDraftCreatedSchema,
  sellerDraftCreateRequestSchema,
  sellerDraftSnapshotSchema,
  sellerAnalysisFindingSchema,
  sellerAnalysisSummarySchema,
  sellerAppealSchema,
  sellerAppealRationaleSchema,
  appealRequestSchema,
  sellerVersionDecisionSchema,
  sellerVersionReviewSchema,
  sellerSubmissionCreatedSchema,
  sellerSubmissionWithdrawnSchema,
  sellerEvidenceEditRequestSchema,
  sellerResponseRequestSchema,
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

describe('server review eligibility', () => {
  const summary = {
    assetId: '123e4567-e89b-12d3-a456-426614174000',
    coverageReview: null,
    requiredResponseCount: 0,
    outstandingResponseCount: 0,
    evidenceRevision: 0,
    assetVersionId: '123e4567-e89b-12d3-a456-426614174001',
    lifecycle: 'COMPLETED',
    runId: '123e4567-e89b-12d3-a456-426614174002',
    reportId: '123e4567-e89b-12d3-a456-426614174003',
    reportIsCurrent: true,
    coverageLevel: 'FULL',
    analyzedFileCount: 1,
    omissionCount: 0,
    findingCount: 0,
    findingDetailsAvailable: true,
    createdAt: null,
    completedAt: null,
    errorCode: null,
    errorMessage: null,
    actionHint: null,
    canRetry: false,
    retryExpectation: null,
    canSubmit: true,
    reviewEligible: true,
    submissionBlockedReason: null,
    comparisonScope: 'Selected source files only',
    limitations: ['Publication requires an explicit moderator decision.'],
    stages: [
      {
        stage: 'VERIFIER',
        applicability: 'MANDATORY',
        outcome: 'NOT_APPLICABLE',
        retryable: false,
      },
    ],
  }

  it('accepts server-issued eligibility without publishing the version', () => {
    expect(sellerAnalysisSummarySchema.parse(summary).canSubmit).toBe(true)
  })

  it('preserves explicit blocked reasons and denied eligibility', () => {
    const blocked = sellerAnalysisSummarySchema.parse({
      ...summary,
      canSubmit: false,
      reviewEligible: false,
      submissionBlockedReason: 'ERR_REVIEW_COVERAGE_REQUIRED',
    })
    expect(blocked.submissionBlockedReason).toBe('ERR_REVIEW_COVERAGE_REQUIRED')
  })

  it('rejects string review eligibility flags', () => {
    expect(sellerAnalysisSummarySchema.safeParse({ ...summary, canSubmit: 'true' }).success).toBe(
      false,
    )
    expect(
      sellerAnalysisSummarySchema.safeParse({ ...summary, reviewEligible: 'true' }).success,
    ).toBe(false)
  })
})

describe('seller submission lifecycle responses', () => {
  it('accepts only the strict created and withdrawn response shapes', () => {
    expect(
      sellerSubmissionCreatedSchema.safeParse({
        submissionId: '123e4567-e89b-12d3-a456-426614174000',
      }).success,
    ).toBe(true)
    expect(
      sellerSubmissionCreatedSchema.safeParse({
        submissionId: '123e4567-e89b-12d3-a456-426614174000',
        state: 'SUBMITTED',
      }).success,
    ).toBe(false)
    expect(
      sellerSubmissionWithdrawnSchema.safeParse({
        status: 'SUCCEEDED',
        caseRevision: 2,
        idempotentResultJson: null,
        replayed: false,
      }).success,
    ).toBe(true)
    expect(
      sellerSubmissionWithdrawnSchema.safeParse({
        status: 'SUCCEEDED',
        caseRevision: 2,
        idempotentResultJson: null,
        replayed: false,
        moderatorId: 'private',
      }).success,
    ).toBe(false)
  })
})

const DECLARATION: SourceDeclarationPayload = {
  ownContributionSummary: 'Authored the pipeline myself.',
  ownChanges: null,
  earlierWork: null,
  redistributionAcknowledged: true,
  disclosurePolicyVersion: '1',
  components: [COMPONENT],
}

describe('sellerAnalysisFindingSchema', () => {
  it('accepts exact backend maximum bounds and public attribution without a link', () => {
    const result = sellerAnalysisFindingSchema.safeParse({
      id: '123e4567-e89b-12d3-a456-426614174010',
      findingType: 'LICENSE_CONCERN',
      ownPath: 'a'.repeat(1024),
      ownExcerpt: 'own source',
      startByte: null,
      endByte: null,
      startLine: null,
      endLine: null,
      reason: 'r'.repeat(1024),
      requestedAction: 'a'.repeat(512),
      responseRequired: false,
      hasResponse: false,
      referenceKind: 'FOREIGN_PUBLIC',
      publicAttribution: { label: 'Public reference', url: null },
    })

    expect(result.success).toBe(true)
  })

  it('rejects values beyond backend bounds and non-HTTPS attribution links', () => {
    const base = {
      id: '123e4567-e89b-12d3-a456-426614174010',
      findingType: 'LICENSE_CONCERN',
      ownPath: 'path',
      ownExcerpt: '',
      startByte: null,
      endByte: null,
      startLine: null,
      endLine: null,
      reason: '',
      requestedAction: '',
      responseRequired: false,
      hasResponse: false,
      referenceKind: 'FOREIGN_PUBLIC',
      publicAttribution: { label: 'Public reference', url: 'http://example.com' },
    }
    expect(
      sellerAnalysisFindingSchema.safeParse({ ...base, ownPath: 'a'.repeat(1025) }).success,
    ).toBe(false)
    expect(sellerAnalysisFindingSchema.safeParse(base).success).toBe(false)
  })
})

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
  it('rejects missing appeal fields and unknown appeal input', () => {
    const body = {
      workspaceId: '123e4567-e89b-12d3-a456-426614174000',
      operationId: '123e4567-e89b-12d3-a456-426614174001',
      expectedWorkspaceRevision: 1,
      expectedCaseRevision: 1,
      rationale: '  Please review  ',
    }
    expect(appealRequestSchema.parse(body).rationale).toBe('Please review')
    expect(appealRequestSchema.safeParse({ ...body, expectedWorkspaceRevision: 0 }).success).toBe(
      false,
    )
    expect(appealRequestSchema.safeParse({ ...body, expectedCaseRevision: 0 }).success).toBe(false)
    expect(appealRequestSchema.safeParse({ ...body, unexpected: true }).success).toBe(false)
    expect(appealRequestSchema.safeParse({ ...body, operationId: undefined }).success).toBe(false)
  })
  it('validates appeal state and timestamps without private details', () => {
    expect(
      sellerAppealSchema.safeParse({
        state: 'PENDING_REVIEW',
        requestedAt: '2026-10-31T10:00:00Z',
        decidedAt: null,
      }).success,
    ).toBe(true)
    expect(
      sellerAppealSchema.safeParse({
        state: 'UNKNOWN',
        requestedAt: '2026-10-31T10:00:00Z',
        decidedAt: null,
      }).success,
    ).toBe(false)
    expect(
      sellerAppealSchema.safeParse({
        state: 'UPHELD',
        requestedAt: '2026-10-31T10:00:00Z',
        decidedAt: null,
        reviewerId: 'private',
      }).success,
    ).toBe(false)
  })
  it('accepts a review payload with blocked reasons', () => {
    const result = sellerVersionReviewSchema.safeParse({
      assetId: '123e4567-e89b-12d3-a456-426614174000',
      coverageReview: null,
      requiredResponseCount: 0,
      outstandingResponseCount: 0,
      evidenceRevision: 0,
      assetVersionId: '123e4567-e89b-12d3-a456-426614174001',
      versionNumber: 1,
      processingStatus: 'PENDING_INSPECTION',
      processingErrorCode: null,
      processingErrorSummary: null,
      analysis: 'ANALYSIS_NOT_AVAILABLE',
      moderationState: 'DRAFT',
      canAppeal: false,
      appeal: null,
      activeSubmission: null,
      history: [],
      publicationEligible: false,
      canUpload: true,
      canSubmit: false,
      blockedReasons: ['ERR_ANALYSIS_NOT_AVAILABLE'],
    })

    expect(result.success).toBe(true)
  })

  it('validates active submissions and bounded owner-safe history entries', () => {
    const result = sellerVersionReviewSchema.safeParse({
      assetId: '123e4567-e89b-12d3-a456-426614174000',
      coverageReview: null,
      requiredResponseCount: 0,
      outstandingResponseCount: 0,
      evidenceRevision: 0,
      assetVersionId: '123e4567-e89b-12d3-a456-426614174001',
      versionNumber: 1,
      processingStatus: 'READY',
      processingErrorCode: null,
      processingErrorSummary: null,
      analysis: 'ANALYSIS_NOT_AVAILABLE',
      moderationState: 'IN_REVIEW',
      canAppeal: false,
      appeal: null,
      activeSubmission: {
        submissionId: '123e4567-e89b-12d3-a456-426614174002',
        kind: 'APPEAL',
        state: 'IN_REVIEW',
        caseRevision: 7,
      },
      history: [
        {
          submissionId: '123e4567-e89b-12d3-a456-426614174002',
          kind: 'APPEAL',
          state: 'IN_REVIEW',
          decisionOutcome: null,
          decisionReason: null,
          createdAt: '2026-10-31T10:00:00Z',
          decidedAt: null,
        },
      ],
      publicationEligible: false,
      canUpload: true,
      canSubmit: false,
      blockedReasons: [],
    })

    expect(result.success).toBe(true)
    if (result.success) {
      expect(result.data.activeSubmission?.kind).toBe('APPEAL')
      expect(result.data.history[0]?.submissionId).toBe('123e4567-e89b-12d3-a456-426614174002')
    }
    expect(
      sellerVersionReviewSchema.safeParse({
        ...(result.success ? result.data : {}),
        history: [
          {
            submissionId: '123e4567-e89b-12d3-a456-426614174002',
            kind: 'STANDARD',
            state: 'REJECTED',
            decisionOutcome: 'REJECTED',
            decisionReason: null,
            createdAt: '2026-10-31T10:00:00Z',
            decidedAt: '2026-10-31T11:00:00Z',
            moderatorId: 'private',
          },
        ],
      }).success,
    ).toBe(false)
    expect(
      sellerVersionReviewSchema.safeParse({ ...(result.success ? result.data : {}), history: [] })
        .success,
    ).toBe(true)
  })

  it('does not retain moderator identity, dispositions, or scores from an unexpected review payload', () => {
    const payload = {
      assetId: '123e4567-e89b-12d3-a456-426614174000',
      coverageReview: null,
      requiredResponseCount: 0,
      outstandingResponseCount: 0,
      evidenceRevision: 0,
      assetVersionId: '123e4567-e89b-12d3-a456-426614174001',
      versionNumber: 1,
      processingStatus: 'READY',
      processingErrorCode: null,
      processingErrorSummary: null,
      analysis: 'ANALYSIS_AVAILABLE',
      moderationState: 'APPROVED',
      canAppeal: false,
      appeal: null,
      activeSubmission: null,
      history: [],
      publicationEligible: true,
      canUpload: true,
      canSubmit: false,
      blockedReasons: [],
      reviewerId: 'private-reviewer-id',
      dispositions: [{ findingId: 'private', value: 'CHANGES_NEEDED' }],
      scores: [0.99],
    }
    const parsed = sellerVersionReviewSchema.safeParse(payload)

    expect(parsed.success).toBe(true)
    if (parsed.success) {
      expect(parsed.data).not.toHaveProperty('reviewerId')
      expect(parsed.data).not.toHaveProperty('dispositions')
      expect(parsed.data).not.toHaveProperty('scores')
    }
  })

  it('accepts a review payload with and without a seller decision projection', () => {
    const base = {
      assetId: '123e4567-e89b-12d3-a456-426614174000',
      coverageReview: null,
      requiredResponseCount: 0,
      outstandingResponseCount: 0,
      evidenceRevision: 0,
      assetVersionId: '123e4567-e89b-12d3-a456-426614174001',
      versionNumber: 1,
      processingStatus: 'READY',
      processingErrorCode: null,
      processingErrorSummary: null,
      analysis: 'ANALYSIS_AVAILABLE',
      moderationState: 'CHANGES_REQUESTED',
      canAppeal: false,
      appeal: null,
      activeSubmission: null,
      history: [],
      publicationEligible: false,
      canUpload: true,
      canSubmit: true,
      blockedReasons: [],
    }
    expect(sellerVersionReviewSchema.safeParse(base).success).toBe(true)
    expect(sellerVersionReviewSchema.safeParse({ ...base, canAppeal: undefined }).success).toBe(
      false,
    )
    expect(
      sellerVersionReviewSchema.safeParse({
        ...base,
        canAppeal: false,
        appeal: { state: 'PENDING_REVIEW', requestedAt: '2026-10-31T10:00:00Z', decidedAt: null },
      }).success,
    ).toBe(true)
    expect(sellerVersionReviewSchema.safeParse({ ...base, decision: null }).success).toBe(true)
    expect(
      sellerVersionReviewSchema.safeParse({
        ...base,
        decision: {
          outcome: 'CHANGES_REQUESTED',
          reason: 'Explain the similar block.',
          decidedAt: '2026-10-31T10:00:00+02:00',
          publication: null,
        },
      }).success,
    ).toBe(true)
  })

  it('accepts each decision outcome and rejects unknown outcomes or publication values', () => {
    const decision = { reason: null, decidedAt: '2026-10-31T10:00:00Z', publication: 'PUBLISHED' }
    expect(
      sellerVersionDecisionSchema.safeParse({ ...decision, outcome: 'APPROVED' }).success,
    ).toBe(true)
    expect(
      sellerVersionDecisionSchema.safeParse({
        ...decision,
        outcome: 'REJECTED',
        reason: 'Reason',
        publication: null,
      }).success,
    ).toBe(true)
    expect(sellerVersionDecisionSchema.safeParse({ ...decision, outcome: 'PENDING' }).success).toBe(
      false,
    )
    expect(
      sellerVersionDecisionSchema.safeParse({
        ...decision,
        outcome: 'APPROVED',
        publication: 'LIVE',
      }).success,
    ).toBe(false)
  })
  it('accepts analysis timestamps with backend UTC offsets', () => {
    const result = sellerVersionReviewSchema.safeParse({
      assetId: '123e4567-e89b-12d3-a456-426614174000',
      coverageReview: null,
      requiredResponseCount: 0,
      outstandingResponseCount: 0,
      evidenceRevision: 0,
      assetVersionId: '123e4567-e89b-12d3-a456-426614174001',
      versionNumber: 1,
      processingStatus: 'READY',
      processingErrorCode: null,
      processingErrorSummary: null,
      analysis: 'ANALYSIS_AVAILABLE',
      moderationState: 'DRAFT',
      canAppeal: false,
      appeal: null,
      activeSubmission: null,
      history: [],
      publicationEligible: false,
      canUpload: true,
      canSubmit: false,
      blockedReasons: ['ERR_REVIEW_COVERAGE_REQUIRED'],
      analysisDetails: {
        assetId: '123e4567-e89b-12d3-a456-426614174000',
        coverageReview: null,
        requiredResponseCount: 0,
        outstandingResponseCount: 0,
        evidenceRevision: 0,
        assetVersionId: '123e4567-e89b-12d3-a456-426614174001',
        lifecycle: 'COMPLETED',
        runId: '123e4567-e89b-12d3-a456-426614174002',
        reportId: '123e4567-e89b-12d3-a456-426614174003',
        reportIsCurrent: true,
        coverageLevel: 'PARTIAL',
        analyzedFileCount: 1,
        omissionCount: 3,
        findingCount: 1,
        findingDetailsAvailable: true,
        createdAt: '2026-10-15T21:29:45.1519+00:00',
        completedAt: '2026-10-15T21:29:55.754256+00:00',
        errorCode: null,
        errorMessage: null,
        actionHint: null,
        canRetry: false,
        retryExpectation: null,
        submissionBlockedReason: 'ERR_REVIEW_COVERAGE_REQUIRED',
        canSubmit: false,
        reviewEligible: false,
        comparisonScope: 'Selected source files from this version only',
        limitations: ['Research evaluation only.'],
        stages: [
          { stage: 'REPORT', applicability: 'MANDATORY', outcome: 'SUCCEEDED', retryable: false },
        ],
      },
    })

    expect(result.success).toBe(true)
  })

  it('rejects numeric enum values instead of canonical strings', () => {
    const result = sellerVersionReviewSchema.safeParse({
      assetId: '123e4567-e89b-12d3-a456-426614174000',
      coverageReview: null,
      requiredResponseCount: 0,
      outstandingResponseCount: 0,
      evidenceRevision: 0,
      assetVersionId: '123e4567-e89b-12d3-a456-426614174001',
      versionNumber: 1,
      processingStatus: 0,
      processingErrorCode: null,
      processingErrorSummary: null,
      analysis: 0,
      moderationState: 1,
      canAppeal: false,
      appeal: null,
      activeSubmission: null,
      history: [],
      publicationEligible: false,
      canUpload: true,
      canSubmit: false,
      blockedReasons: [],
    })

    expect(result.success).toBe(false)
  })

  it('mirrors appeal trim-then-measure and NUL validation', () => {
    expect(sellerAppealRationaleSchema.parse(`  ${'a'.repeat(2000)}  `)).toBe('a'.repeat(2000))
    expect(sellerAppealRationaleSchema.safeParse('   ').success).toBe(false)
    expect(sellerAppealRationaleSchema.safeParse(` ${'a'.repeat(2001)} `).success).toBe(false)
    expect(sellerAppealRationaleSchema.safeParse(`${' '.repeat(4001)}a`).success).toBe(false)
    expect(sellerAppealRationaleSchema.safeParse('valid\0text').success).toBe(false)
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
      coverageReview: null,
      requiredResponseCount: 0,
      outstandingResponseCount: 0,
      evidenceRevision: 0,
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

describe('seller evidence request byte bounds', () => {
  const evidence = {
    workspaceId: '123e4567-e89b-12d3-a456-426614174000',
    reportId: '123e4567-e89b-12d3-a456-426614174001',
    operationId: '123e4567-e89b-12d3-a456-426614174002',
    expectedWorkspaceRevision: 1,
    expectedEvidenceRevision: 0,
    text: ' '.repeat(1000) + 'x'.repeat(1000),
  }

  it('bounds raw evidence text before trimming for response and evidence routes', () => {
    expect(sellerEvidenceEditRequestSchema.safeParse(evidence).success).toBe(true)
    expect(
      sellerEvidenceEditRequestSchema.safeParse({ ...evidence, text: ' '.repeat(2001) }).success,
    ).toBe(false)
    expect(
      sellerResponseRequestSchema.safeParse({
        ...evidence,
        findingId: '123e4567-e89b-12d3-a456-426614174003',
        responseType: 'NO_EVIDENCE',
      }).success,
    ).toBe(true)
    expect(
      sellerResponseRequestSchema.safeParse({
        ...evidence,
        findingId: '123e4567-e89b-12d3-a456-426614174003',
        responseType: 'NO_EVIDENCE',
        text: ' '.repeat(2001),
      }).success,
    ).toBe(false)
  })
})
