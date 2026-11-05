import { describe, expect, it } from 'vitest'
import {
  isVersionPublished,
  versionReviewBlockedReasonMessages,
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
  it('covers each mapped seller review blocker with nonempty code-free resolver output', () => {
    const expectedMessages = {
      ERR_COVERAGE_ASSESSMENT_BLOCKED:
        'Coverage review could not establish a safe manual review scope. Correct the source package before requesting review again.',
      ERR_COVERAGE_ASSESSMENT_INVALIDATED:
        'Coverage review no longer matches this version. Request review for the current report and declaration.',
      ERR_COVERAGE_ASSESSOR_UNAVAILABLE:
        'Coverage review needs a new reviewer. Request another coverage review.',
      ERR_FINDING_RESPONSE_REQUIRED:
        'Respond to every required finding, or choose No evidence, before submitting this version.',
      ERR_SELLER_EVIDENCE_STALE: 'Review evidence changed. Refresh this version and try again.',
      ERR_VERSION_FILE_CHECKS_INCOMPLETE: 'File checks are not complete for this version yet.',
      ERR_ANALYSIS_NOT_AVAILABLE: 'Code analysis is not available yet for this version.',
      ERR_DECLARATION_INCOMPLETE: 'The source declaration is incomplete for this version.',
      ERR_REVIEW_COVERAGE_REQUIRED:
        'Incomplete coverage requires a coverage assessment. Submission is currently blocked.',
      ERR_MODERATION_ACTIVE_CASE_EXISTS: 'This version already has an active moderation case.',
      ERR_ANALYSIS_REPORT_NOT_TRUSTED: 'The report is not eligible for submission.',
      ERR_MODERATION_WORKSPACE_STALE:
        'The draft changed. Refresh version review before submitting.',
      ERR_MODERATION_APPEAL_REQUIRED: 'Request an independent review of the rejection instead.',
      ERR_MODERATION_REJECTION_FINAL:
        'This rejection is final. Submit a substantively corrected review request.',
      ERR_MODERATION_APPEAL_BASIS_CHANGED: 'The review basis changed. Submit a new review request.',
      ERR_MODERATION_APPEAL_EXISTS: 'An appeal already exists for this decision.',
      ERR_MODERATION_APPEAL_NOT_ALLOWED: 'An appeal is not available for this review.',
    } satisfies Record<string, string>

    expect(Object.keys(versionReviewBlockedReasonMessages).sort()).toEqual(
      Object.keys(expectedMessages).sort(),
    )
    for (const [code, expected] of Object.entries(expectedMessages)) {
      expect(versionReviewBlockedReasonMessage(code), code).toBe(expected)
      expect(versionReviewActionMessage(code), code).toBe(expected)
      expect(versionReviewBlockedReasonMessage(code)).not.toMatch(/ERR_[A-Z0-9_]+/)
    }
  })

  it('uses explicit generic fallbacks for unknown seller review codes', () => {
    expect(versionReviewBlockedReasonMessage('ERR_FUTURE_REVIEW_CODE')).toBe(
      'Submission is not available for this version yet.',
    )
    expect(versionReviewActionMessage('ERR_FUTURE_REVIEW_CODE')).toBe(
      'The review request could not be completed.',
    )
  })

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
