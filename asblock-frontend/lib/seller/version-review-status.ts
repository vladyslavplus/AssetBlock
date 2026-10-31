import type { SellerVersionReview } from '@/lib/seller/seller-draft-schemas'

/** Messages mirror the backend `ErrorCodesToErrorMessages` mapping. */
export const versionReviewBlockedReasonMessages: Record<string, string> = {
  ERR_VERSION_FILE_CHECKS_INCOMPLETE: 'File checks are not complete for this version yet.',
  ERR_ANALYSIS_NOT_AVAILABLE: 'Code analysis is not available yet for this version.',
  ERR_DECLARATION_INCOMPLETE: 'The source declaration is incomplete for this version.',
  ERR_REVIEW_COVERAGE_REQUIRED:
    'Incomplete coverage requires a coverage assessment. Submission is currently blocked.',
  ERR_MODERATION_ACTIVE_CASE_EXISTS: 'This version already has an active moderation case.',
  ERR_ANALYSIS_REPORT_NOT_TRUSTED: 'The report is not eligible for submission.',
  ERR_MODERATION_WORKSPACE_STALE: 'The draft changed. Refresh version review before submitting.',
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
    case 'WITHDRAWN':
      return 'Withdrawn'
  }
}

export function isVersionPublished(review: SellerVersionReview): boolean {
  return review.publicationEligible
}
