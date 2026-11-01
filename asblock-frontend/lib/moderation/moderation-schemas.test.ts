import { describe, expect, it } from 'vitest'
import {
  claimChangeRequestSchema,
  claimChangeResponseSchema,
  moderationCaseSummarySchema,
  moderationDecisionRequestSchema,
  moderationDecisionResponseSchema,
  moderationQueueQuerySchema,
  moderatorQueuePageSchema,
  coverageAssessmentRequestSchema,
  coverageAssessmentResponseSchema,
  coverageQueuePageSchema,
  restrictedEvidenceResponseSchema,
} from './moderation-schemas'

const id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const hash = 'a'.repeat(64)
const queueItem = {
  submissionId: id,
  assetId: id,
  assetTitle: 'Asset title',
  versionNumber: 2,
  submittedAt: '2026-11-01T10:00:00Z',
  state: 'SUBMITTED',
  kind: 'STANDARD',
  claimHolderId: null,
  claimHolderName: null,
  caseRevision: 1,
  coverageLevel: 'FULL',
  materialFindingCount: 0,
  hasOpenResponses: false,
}

describe('moderation schemas', () => {
  it('normalizes queue query defaults and rejects unsupported filters or fields', () => {
    expect(moderationQueueQuerySchema.parse({})).toEqual({
      claimFilter: 'ALL',
      pageSize: 50,
    })
    expect(moderationQueueQuerySchema.safeParse({ state: 'APPROVED' }).success).toBe(false)
    expect(moderationQueueQuerySchema.safeParse({ ignored: 'value' }).success).toBe(false)
  })

  it('strictly validates queue response and cursor shape', () => {
    expect(
      moderatorQueuePageSchema.parse({ items: [queueItem], nextCursor: null }).items,
    ).toHaveLength(1)
    expect(
      moderatorQueuePageSchema.safeParse({ items: [queueItem], nextCursor: null, appeal: true })
        .success,
    ).toBe(false)
    expect(
      moderatorQueuePageSchema.safeParse({
        items: [{ ...queueItem, claimHolderId: 1 }],
        nextCursor: null,
      }).success,
    ).toBe(false)
  })

  it('strictly validates claim, case and decision contracts', () => {
    expect(
      claimChangeRequestSchema.safeParse({ operationId: id, expectedCaseRevision: 1 }).success,
    ).toBe(true)
    expect(
      claimChangeRequestSchema.safeParse({ operationId: id, expectedCaseRevision: 1, extra: true })
        .success,
    ).toBe(false)
    expect(
      claimChangeResponseSchema.safeParse({
        submissionId: id,
        state: 'IN_REVIEW',
        holderId: id,
        caseRevision: 2,
        replayed: false,
      }).success,
    ).toBe(true)
    expect(
      claimChangeResponseSchema.safeParse({
        submissionId: id,
        state: 'IN_REVIEW',
        holderId: id,
        caseRevision: 2,
        replayed: false,
        privateText: 'not allowed',
      }).success,
    ).toBe(false)
    expect(
      moderationCaseSummarySchema.safeParse({
        submissionId: id,
        assetId: id,
        assetVersionId: id,
        state: 'IN_REVIEW',
        kind: 'STANDARD',
        caseRevision: 2,
        contentSha256: hash,
        policyVersion: 'policy-v1',
        basis: {
          versionId: id,
          contentSha256: hash,
          analysisReportId: id,
          declarationRevision: 1,
          materialMetadataRevision: 1,
          policyVersion: 'policy-v1',
          sellerEvidenceRevision: 0,
          caseRevision: 2,
        },
        manualPlanCompletionReferenceRequired: false,
        retrievalEvidence: null,
        coverageAssessmentId: null,
        isClaimedByCaller: true,
      }).success,
    ).toBe(true)
    const decisionRequest = {
      operationId: id,
      basis: {
        versionId: id,
        contentSha256: hash,
        analysisReportId: id,
        declarationRevision: 1,
        materialMetadataRevision: 1,
        policyVersion: 'policy-v1',
        sellerEvidenceRevision: 0,
        caseRevision: 1,
      },
      outcome: 'APPROVE',
      rationale: 'Reviewed',
      dispositions: [],
    }
    expect(moderationDecisionRequestSchema.safeParse(decisionRequest).success).toBe(true)
    expect(
      moderationDecisionRequestSchema.safeParse({
        ...decisionRequest,
        manualPlanCompletionReference: `invalid\0reference`,
      }).success,
    ).toBe(false)
    expect(
      moderationDecisionRequestSchema.safeParse({
        ...decisionRequest,
        manualPlanCompletionReference: ` ${'x'.repeat(200)} `,
      }).success,
    ).toBe(true)
    expect(
      moderationDecisionRequestSchema.safeParse({
        operationId: id,
        basis: {
          versionId: id,
          contentSha256: hash,
          analysisReportId: id,
          declarationRevision: 0,
          materialMetadataRevision: 1,
          policyVersion: 'policy-v1',
          sellerEvidenceRevision: 0,
          caseRevision: 1,
        },
        outcome: 'APPROVE',
        rationale: 'Reviewed',
        dispositions: [],
      }).success,
    ).toBe(false)
    expect(
      moderationDecisionResponseSchema.safeParse({
        decisionId: id,
        state: 'APPROVED',
        publicationSnapshotId: null,
        promoted: false,
        caseRevision: 2,
        replayed: false,
      }).success,
    ).toBe(true)
  })

  it('strictly validates restricted evidence and coverage assessment contracts', () => {
    expect(
      restrictedEvidenceResponseSchema.safeParse({
        scopeId: id,
        reportId: id,
        kind: 'DECLARATION',
        findingId: null,
        content: '{"declaration":true}',
        itemCount: 1,
        truncated: false,
      }).success,
    ).toBe(true)
    expect(
      restrictedEvidenceResponseSchema.safeParse({
        scopeId: id,
        reportId: id,
        kind: 'DECLARATION',
        findingId: null,
        content: 'private',
        itemCount: 1,
        truncated: false,
        cached: true,
      }).success,
    ).toBe(false)
    expect(
      coverageQueuePageSchema.safeParse({
        items: [
          {
            requestId: id,
            assetVersionId: id,
            reportId: id,
            requestRevision: 3,
            tupleDigest: hash,
            createdAt: '2026-11-01T10:00:00Z',
          },
        ],
        page: 1,
        pageSize: 20,
        totalCount: 1,
        totalPages: 1,
      }).success,
    ).toBe(true)
    const assessment = {
      operationId: id,
      expectedRequestRevision: 3,
      expectedTupleDigest: hash,
      outcome: 'BLOCKED',
      competence: ['Asset structure review'],
      scope: [],
      manageableScope: false,
      reviewPlan: 'Manual inspection plan',
      completedPlanReference: '',
      rationale: 'Source scope is not readable.',
    }
    expect(coverageAssessmentRequestSchema.safeParse(assessment).success).toBe(true)
    expect(
      coverageAssessmentRequestSchema.safeParse({ ...assessment, ignored: true }).success,
    ).toBe(false)
    expect(
      coverageAssessmentResponseSchema.safeParse({
        requestId: id,
        requestRevision: 4,
        workspaceRevision: 5,
        tupleDigest: hash,
        replayed: false,
      }).success,
    ).toBe(true)
  })

  it('strictly validates frozen case basis and bounded per-finding responses', () => {
    const casePayload = {
      submissionId: id,
      assetId: id,
      assetVersionId: id,
      state: 'IN_REVIEW',
      kind: 'STANDARD',
      caseRevision: 2,
      contentSha256: hash,
      policyVersion: 'policy-v1',
      basis: {
        versionId: id,
        contentSha256: hash,
        analysisReportId: id,
        declarationRevision: 1,
        materialMetadataRevision: 1,
        policyVersion: 'policy-v1',
        sellerEvidenceRevision: 0,
        caseRevision: 2,
      },
      manualPlanCompletionReferenceRequired: true,
      isClaimedByCaller: true,
      coverageAssessmentId: id,
      retrievalEvidence: {
        reportId: id,
        scoresAvailability: 'AVAILABLE',
        referenceSnapshotId: null,
        referenceRevision: null,
        coverageLevel: 'FULL',
        omissionCount: 0,
        findingOverflow: 0,
        retrieval: null,
        responseRequirementsAvailable: true,
        candidates: [
          {
            findingId: id,
            findingKey: 'finding-1',
            findingType: 'SIMILARITY_TOKEN',
            ownScopePath: 'src/file.py',
            ownStartByte: null,
            ownEndByte: null,
            ownStartLine: null,
            ownEndLine: null,
            referenceItemId: null,
            referenceOrigin: null,
            referenceSensitivity: 'PUBLIC',
            scoresAvailability: 'AVAILABLE',
            scores: null,
            responseRequired: true,
            answered: true,
            responses: [
              { kind: 'PRIOR_WORK', text: 'Bounded answer', createdAt: '2026-11-01T10:00:00Z' },
            ],
          },
        ],
      },
    }
    expect(moderationCaseSummarySchema.safeParse(casePayload).success).toBe(true)
    expect(
      moderationCaseSummarySchema.safeParse({
        ...casePayload,
        expectedManualPlanCompletionReference: 'do-not-expose',
      }).success,
    ).toBe(false)
    expect(
      moderationCaseSummarySchema.safeParse({
        ...casePayload,
        retrievalEvidence: {
          ...casePayload.retrievalEvidence,
          candidates: [{ ...casePayload.retrievalEvidence.candidates[0], findingType: 'UNKNOWN' }],
        },
      }).success,
    ).toBe(false)
    expect(
      moderationCaseSummarySchema.safeParse({
        ...casePayload,
        retrievalEvidence: { ...casePayload.retrievalEvidence, reportId: null },
      }).success,
    ).toBe(false)
    expect(
      moderationCaseSummarySchema.safeParse({
        ...casePayload,
        basis: { ...casePayload.basis, privateOwnerId: id },
      }).success,
    ).toBe(false)
  })
})
