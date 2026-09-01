import type { PurchaseSource } from '@/lib/library/library-schemas'

export {
  type PurchaseSource,
  type PurchaseLibraryItem,
  type PagedPurchaseLibraryDto,
  purchaseSourceSchema,
  purchaseLibraryItemSchema,
  pagedPurchaseLibraryResponseSchema,
} from '@/lib/library/library-schemas'

/** Normalize backend string enum to ASSET | BUNDLE. */
export function normalizePurchaseSource(raw: unknown): PurchaseSource {
  if (raw === 'BUNDLE') return 'BUNDLE'
  return 'ASSET'
}
