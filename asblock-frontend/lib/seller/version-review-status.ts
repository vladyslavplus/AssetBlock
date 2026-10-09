import type { SellerVersionReview } from '@/lib/seller/seller-draft-schemas'

/** Messages mirror the backend `ErrorCodesToErrorMessages` mapping. */
export const versionReviewBlockedReasonMessages: Record<string, string> = {
  ERR_VERSION_FILE_CHECKS_INCOMPLETE: 'File checks are not complete for this version yet.',
  ERR_ANALYSIS_NOT_AVAILABLE: 'Code analysis is not available yet for this version.',
  ERR_DECLARATION_INCOMPLETE: 'The source declaration is incomplete for this version.',
}

const versionReviewBlockedReasonActions: Record<string, string> = {
  ERR_VERSION_FILE_CHECKS_INCOMPLETE: 'Wait for the archive inspection and malware scan to finish.',
  ERR_ANALYSIS_NOT_AVAILABLE:
    'Keep this version ready — code analysis arrives in a later update. Submission stays disabled until then.',
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
