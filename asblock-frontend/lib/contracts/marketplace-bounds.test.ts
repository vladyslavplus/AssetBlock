import { describe, expect, it } from 'vitest'
import {
  ASSET_CATEGORY_NAME_MAX_LENGTH,
  ASSET_DESCRIPTION_MAX_LENGTH,
  ASSET_MAX_TAGS,
  ASSET_TAG_NAME_MAX_LENGTH,
  ASSET_TITLE_MAX_LENGTH,
  BUNDLE_DESCRIPTION_MAX_LENGTH,
  BUNDLE_MAX_ITEMS,
  BUNDLE_MIN_ITEMS,
  BUNDLE_TITLE_MAX_LENGTH,
  COLLECTION_DESCRIPTION_MAX_LENGTH,
  COLLECTION_MAX_ITEMS,
  COLLECTION_MIN_ITEMS_TO_PUBLISH,
  COLLECTION_TITLE_MAX_LENGTH,
} from '@/lib/contracts/marketplace-bounds'

describe('marketplace-bounds', () => {
  it('exposes canonical asset bounds', () => {
    expect(ASSET_TITLE_MAX_LENGTH).toBe(500)
    expect(ASSET_DESCRIPTION_MAX_LENGTH).toBe(5000)
    expect(ASSET_TAG_NAME_MAX_LENGTH).toBe(50)
    expect(ASSET_MAX_TAGS).toBe(10)
    expect(ASSET_CATEGORY_NAME_MAX_LENGTH).toBe(200)
  })

  it('exposes canonical bundle bounds', () => {
    expect(BUNDLE_TITLE_MAX_LENGTH).toBe(160)
    expect(BUNDLE_DESCRIPTION_MAX_LENGTH).toBe(2000)
    expect(BUNDLE_MIN_ITEMS).toBe(2)
    expect(BUNDLE_MAX_ITEMS).toBe(20)
  })

  it('exposes canonical collection bounds', () => {
    expect(COLLECTION_TITLE_MAX_LENGTH).toBe(160)
    expect(COLLECTION_DESCRIPTION_MAX_LENGTH).toBe(2000)
    expect(COLLECTION_MAX_ITEMS).toBe(50)
    expect(COLLECTION_MIN_ITEMS_TO_PUBLISH).toBe(1)
  })
})
