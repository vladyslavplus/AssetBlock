import { describe, expect, it } from 'vitest'
import { getCollectionStatusBadgeVariant } from '@/lib/collections/collection-ui'

describe('getCollectionStatusBadgeVariant', () => {
  it('maps all collection statuses to their badge variants exhaustively', () => {
    expect(getCollectionStatusBadgeVariant('PUBLISHED')).toBe('default')
    expect(getCollectionStatusBadgeVariant('ARCHIVED')).toBe('outline')
    expect(getCollectionStatusBadgeVariant('DRAFT')).toBe('secondary')
  })
})
