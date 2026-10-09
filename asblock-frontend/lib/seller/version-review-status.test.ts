import { describe, expect, it } from 'vitest'
import {
  isVersionPublished,
  versionReviewBlockedReasonAction,
  versionReviewBlockedReasonMessage,
  versionReviewModerationLabel,
} from '@/lib/seller/version-review-status'
import type { SellerVersionReview } from '@/lib/seller/seller-draft-schemas'

const UUID = '123e4567-e89b-12d3-a456-426614174000'

function review(overrides: Partial<SellerVersionReview> = {}): SellerVersionReview {
  return {
    assetId: UUID,
    assetVersionId: UUID,
    versionNumber: 1,
    processingStatus: 'READY',
    processingErrorCode: null,
    processingErrorSummary: null,
    analysis: 'ANALYSIS_NOT_AVAILABLE',
    moderationState: 'DRAFT',
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
    expect(versionReviewBlockedReasonAction('ERR_ANALYSIS_NOT_AVAILABLE')).toMatch(/later update/i)
    expect(versionReviewBlockedReasonAction('ERR_UNKNOWN')).toBeNull()
  })

  it('labels moderation states without implying publication', () => {
    expect(versionReviewModerationLabel('DRAFT')).toBe('Not submitted')
    expect(versionReviewModerationLabel('APPROVED')).toBe('Approved')
  })

  it('treats publication eligibility as the only publication signal', () => {
    expect(
      isVersionPublished(review({ processingStatus: 'READY', publicationEligible: false })),
    ).toBe(false)
    expect(isVersionPublished(review({ publicationEligible: true }))).toBe(true)
  })
})
