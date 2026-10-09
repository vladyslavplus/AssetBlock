import type { SellerProcessingStatus } from '@/lib/seller/seller-asset-schemas'

export function getSellerProcessingStatusLabel(status: SellerProcessingStatus): string {
  switch (status) {
    case 'PENDING_INSPECTION':
      return 'Inspecting archive'
    case 'PENDING_MALWARE_SCAN':
      return 'Scanning for malware'
    case 'READY':
      return 'Checks passed'
    case 'REJECTED':
      return 'Rejected'
    case 'PROCESSING_FAILED':
      return 'Processing failed'
  }
}

export function getSellerProcessingStatusDescription(status: SellerProcessingStatus): string {
  switch (status) {
    case 'PENDING_INSPECTION':
      return 'This upload is being inspected before buyers can access it.'
    case 'PENDING_MALWARE_SCAN':
      return 'This upload is being scanned before buyers can access it.'
    case 'READY':
      return (
        'Technical file checks passed. This version is not published yet — it needs an ' +
        'approved review before new sales.'
      )
    case 'REJECTED':
      return 'This upload did not pass security checks and is not publicly listed.'
    case 'PROCESSING_FAILED':
      return 'Security processing failed. This listing is not publicly visible.'
  }
}

export function getSellerProcessingBadgeClass(status: SellerProcessingStatus): string {
  switch (status) {
    case 'PENDING_INSPECTION':
    case 'PENDING_MALWARE_SCAN':
      return 'border-amber-500/40 bg-amber-500/10 text-amber-200'
    case 'READY':
      return 'border-emerald-500/40 bg-emerald-500/10 text-emerald-200'
    case 'REJECTED':
    case 'PROCESSING_FAILED':
      return 'border-destructive/40 bg-destructive/10 text-destructive'
  }
}
