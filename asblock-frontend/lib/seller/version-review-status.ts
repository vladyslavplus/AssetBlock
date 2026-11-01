import type { SellerVersionReview } from '@/lib/seller/seller-draft-schemas'

/** Messages mirror the backend `ErrorCodesToErrorMessages` mapping. */
export const versionReviewBlockedReasonMessages: Record<string, string> = {
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
  ERR_MODERATION_WORKSPACE_STALE: 'The draft changed. Refresh version review before submitting.',
  ERR_MODERATION_APPEAL_REQUIRED: 'Request an independent review of the rejection instead.',
  ERR_MODERATION_REJECTION_FINAL:
    'This rejection is final. Submit a substantively corrected review request.',
  ERR_MODERATION_APPEAL_BASIS_CHANGED: 'The review basis changed. Submit a new review request.',
  ERR_MODERATION_APPEAL_EXISTS: 'An appeal already exists for this decision.',
  ERR_MODERATION_APPEAL_NOT_ALLOWED: 'An appeal is not available for this review.',
}

const versionReviewBlockedReasonActions: Record<string, string> = {
  ERR_VERSION_FILE_CHECKS_INCOMPLETE: 'Wait for the archive inspection and malware scan to finish.',
  ERR_ANALYSIS_NOT_AVAILABLE: 'Wait for analysis to complete or use the available retry action.',
  ERR_DECLARATION_INCOMPLETE:
    'Open the source declaration editor and complete the required fields.',
}

export function versionReviewBlockedReasonMessage(code: string): string {
  return (
    versionReviewBlockedReasonMessages[code] ?? 'Submission is not available for this version yet.'
  )
}

export function versionReviewActionMessage(code: string): string {
  return versionReviewBlockedReasonMessages[code] ?? 'The review request could not be completed.'
}

export function versionReviewBlockedReasonAction(code: string): string | null {
  return versionReviewBlockedReasonActions[code] ?? null
}

export function versionReviewModerationLabel(
  state: SellerVersionReview['moderationState'],
): string {
  switch (state) {
    case 'DRAFT':
      return 'Not submitted'
    case 'SUBMITTED':
      return 'Submitted for review'
    case 'IN_REVIEW':
      return 'Review in progress'
    case 'CHANGES_REQUESTED':
      return 'Changes requested'
    case 'APPROVED':
      return 'Approved'
    case 'REJECTED':
      return 'Rejected'
    case 'SUPERSEDED':
      return 'Evidence changed; resubmit for review'
    case 'WITHDRAWN':
      return 'Withdrawn'
  }
}

export function isVersionPublished(review: SellerVersionReview): boolean {
  return review.publicationEligible
}
