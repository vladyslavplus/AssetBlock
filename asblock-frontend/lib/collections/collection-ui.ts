import type { CollectionStatus } from '@/lib/collections/collection-schemas'

export type CollectionBadgeVariant = 'default' | 'secondary' | 'outline'

const COLLECTION_STATUS_VARIANTS = {
  DRAFT: 'secondary',
  PUBLISHED: 'default',
  ARCHIVED: 'outline',
} as const satisfies Record<CollectionStatus, CollectionBadgeVariant>

/**
 * Returns an exhaustive badge variant for typed collection status.
 * - PUBLISHED -> default
 * - ARCHIVED -> outline
 * - DRAFT -> secondary
 */
export function getCollectionStatusBadgeVariant(status: CollectionStatus): CollectionBadgeVariant {
  return COLLECTION_STATUS_VARIANTS[status]
}
