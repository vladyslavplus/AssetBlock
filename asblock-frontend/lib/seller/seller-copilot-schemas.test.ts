import { describe, expect, it } from 'vitest'

import {
  listingCopilotEnqueueResponseSchema,
  listingCopilotSuggestionSchema,
} from '@/lib/seller/seller-copilot-schemas'

const suggestion = {
  jobId: '11111111-1111-4111-8111-111111111111',
  assetVersionId: '22222222-2222-4222-8222-222222222222',
  title: 'Chair',
  description: 'A chair',
  category: '3D',
  tags: ['lowpoly'],
  provider: 'OPENROUTER',
  actualModel: 'fixture/openrouter-test',
  modelRevision: null,
  upstreamProvider: 'TestHost',
  createdAt: '2026-08-25T12:00:00Z',
}

describe('seller-copilot-schemas', () => {
  it('accepts a bounded suggestion and enqueue payload', () => {
    expect(listingCopilotSuggestionSchema.parse(suggestion).title).toBe('Chair')
    expect(
      listingCopilotEnqueueResponseSchema.parse({
        jobId: suggestion.jobId,
        assetVersionId: suggestion.assetVersionId,
      }).jobId,
    ).toBe(suggestion.jobId)
  })

  it('rejects unknown fields and provider request id', () => {
    expect(() => listingCopilotSuggestionSchema.parse({ ...suggestion, extra: true })).toThrow()
    expect(() =>
      listingCopilotSuggestionSchema.parse({ ...suggestion, providerRequestId: 'gen-1' }),
    ).toThrow()
    expect(() =>
      listingCopilotEnqueueResponseSchema.parse({
        jobId: suggestion.jobId,
        assetVersionId: suggestion.assetVersionId,
        secret: 'x',
      }),
    ).toThrow()
  })

  it('rejects values outside backend bounds', () => {
    expect(() => listingCopilotSuggestionSchema.parse({ ...suggestion, title: '' })).toThrow()
    expect(() =>
      listingCopilotSuggestionSchema.parse({ ...suggestion, title: 'x'.repeat(501) }),
    ).toThrow()
    expect(() =>
      listingCopilotSuggestionSchema.parse({ ...suggestion, description: 'x'.repeat(5001) }),
    ).toThrow()
    expect(() => listingCopilotSuggestionSchema.parse({ ...suggestion, category: '' })).toThrow()
    expect(() =>
      listingCopilotSuggestionSchema.parse({ ...suggestion, tags: ['x'.repeat(51)] }),
    ).toThrow()
    expect(() =>
      listingCopilotSuggestionSchema.parse({
        ...suggestion,
        tags: Array.from({ length: 11 }, (_, i) => `t${i}`),
      }),
    ).toThrow()
    expect(() => listingCopilotSuggestionSchema.parse({ ...suggestion, actualModel: '' })).toThrow()
    expect(() =>
      listingCopilotSuggestionSchema.parse({ ...suggestion, modelRevision: 'x'.repeat(201) }),
    ).toThrow()
    expect(() =>
      listingCopilotSuggestionSchema.parse({ ...suggestion, upstreamProvider: 'x'.repeat(129) }),
    ).toThrow()
  })
})
