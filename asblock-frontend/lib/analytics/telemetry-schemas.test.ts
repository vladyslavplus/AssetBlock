import { describe, expect, it } from 'vitest'

import { ingestRecommendationEventBrowserSchema } from '@/lib/analytics/telemetry-schemas'

const token = 'a'.repeat(64)

describe('ingestRecommendationEventBrowserSchema', () => {
  it('accepts a well-formed impression envelope', () => {
    const parsed = ingestRecommendationEventBrowserSchema.safeParse({
      eventId: '11111111-1111-4111-8111-111111111111',
      eventType: 'IMPRESSION',
      sourceAssetId: '22222222-2222-4222-8222-222222222222',
      targetAssetId: '33333333-3333-4333-8333-333333333333',
      slotPosition: 0,
      exposureId: '44444444-4444-4444-8444-444444444444',
      rankingVersion: 'similar-assets-v1-metadata',
      expiresAt: '2026-09-13T12:15:00.000Z',
      exposureToken: token,
      candidateIds: ['33333333-3333-4333-8333-333333333333'],
      deviceClass: 'DESKTOP',
    })
    expect(parsed.success).toBe(true)
  })

  it('rejects a missing candidate list', () => {
    const parsed = ingestRecommendationEventBrowserSchema.safeParse({
      eventId: '11111111-1111-4111-8111-111111111111',
      eventType: 'CLICK',
      sourceAssetId: '22222222-2222-4222-8222-222222222222',
      targetAssetId: '33333333-3333-4333-8333-333333333333',
      slotPosition: 0,
      exposureId: '44444444-4444-4444-8444-444444444444',
      rankingVersion: 'similar-assets-v1-metadata',
      expiresAt: '2026-09-13T12:15:00.000Z',
      exposureToken: token,
      deviceClass: 'DESKTOP',
    })
    expect(parsed.success).toBe(false)
  })
})
