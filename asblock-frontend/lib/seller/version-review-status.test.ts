import { describe, expect, it } from 'vitest'
import {
  isVersionPublished,
  versionReviewBlockedReasonAction,
  versionReviewBlockedReasonMessage,
  versionReviewActionMessage,
  versionReviewModerationLabel,
} from '@/lib/seller/version-review-status'
import type { SellerVersionReview } from '@/lib/seller/seller-draft-schemas'

const UUID = '123e4567-e89b-12d3-a456-426614174000'

function review(overrides: Partial<SellerVersionReview> = {}): SellerVersionReview {
  return {
    assetId: UUID,
    coverageReview: null,
    requiredResponseCount: 0,
    outstandingResponseCount: 0,
    evidenceRevision: 0,
    assetVersionId: UUID,
    versionNumber: 1,
    processingStatus: 'READY',
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
    blockedReasons: [],
    ...overrides,
  }
}

describe('version review status helpers', () => {
  it('maps backend blocked reason codes to the canonical backend messages', () => {
    expect(versionReviewBlockedReasonMessage('ERR_ANALYSIS_NOT_AVAILABLE')).toBe(
      'Code analysis is not available yet for this version.',
    )
    expect(versionReviewBlockedReasonMessage('ERR_VERSION_FILE_CHECKS_INCOMPLETE')).toBe(
      'File checks are not complete for this version yet.',
    )
    expect(versionReviewBlockedReasonMessage('ERR_DECLARATION_INCOMPLETE')).toBe(
      'The source declaration is incomplete for this version.',
    )
  })

  it('falls back to a safe generic message for unknown codes', () => {
    expect(versionReviewBlockedReasonMessage('ERR_SOMETHING_ELSE')).toBe(
      'Submission is not available for this version yet.',
    )
  })

  it('gives concrete next actions for known reasons', () => {
    expect(versionReviewBlockedReasonAction('ERR_DECLARATION_INCOMPLETE')).toMatch(
      /declaration editor/i,
    )
    expect(versionReviewBlockedReasonAction('ERR_ANALYSIS_NOT_AVAILABLE')).toMatch(/retry action/i)
    expect(versionReviewBlockedReasonAction('ERR_UNKNOWN')).toBeNull()
  })

  it('explains coverage blockers explicitly', () => {
    expect(versionReviewBlockedReasonMessage('ERR_REVIEW_COVERAGE_REQUIRED')).toMatch(
      /coverage assessment/i,
    )
  })

  it('maps appeal and outstanding-response outcomes to seller-facing backend messages', () => {
    expect(versionReviewActionMessage('ERR_FINDING_RESPONSE_REQUIRED')).toBe(
      'Respond to every required finding, or choose No evidence, before submitting this version.',
    )
    expect(versionReviewActionMessage('ERR_MODERATION_APPEAL_BASIS_CHANGED')).toMatch(
      /submit a new review request/i,
    )
    expect(versionReviewActionMessage('ERR_MODERATION_APPEAL_REQUIRED')).toMatch(
      /independent review/i,
    )
    expect(versionReviewActionMessage('ERR_MODERATION_REJECTION_FINAL')).toMatch(
      /substantively corrected/i,
    )
  })

  it('labels moderation states without implying publication', () => {
    const labels = [
      ['DRAFT', 'Not submitted'],
      ['SUBMITTED', 'Submitted for review'],
      ['IN_REVIEW', 'Review in progress'],
      ['CHANGES_REQUESTED', 'Changes requested'],
      ['APPROVED', 'Approved'],
      ['REJECTED', 'Rejected'],
      ['WITHDRAWN', 'Withdrawn'],
      ['SUPERSEDED', 'Evidence changed; resubmit for review'],
    ] as const
    for (const [state, label] of labels) {
      expect(versionReviewModerationLabel(state)).toBe(label)
    }
  })

  it('treats publication eligibility as the only publication signal', () => {
    expect(
      isVersionPublished(review({ processingStatus: 'READY', publicationEligible: false })),
    ).toBe(false)
    expect(isVersionPublished(review({ publicationEligible: true }))).toBe(true)
  })
})
